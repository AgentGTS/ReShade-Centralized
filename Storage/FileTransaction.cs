using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using static ReShadeCentralized.LocalFiles;

namespace ReShadeCentralized;

public sealed class FileTransaction : IDisposable
{
    private readonly string directory;
    private readonly ILayerRegistry? registry;
    private readonly Action<string>? reportWarning;
    private readonly TransactionJournal journal = new();
    private readonly HashSet<string> captured = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions CompactJson = new(ManagerState.Json)
    {
        WriteIndented = false
    };

    private bool done;

    public FileTransaction(
        string recovery,
        string description,
        ILayerRegistry? registry = null,
        Action<string>? reportWarning = null)
    {
        this.registry = registry;
        this.reportWarning = reportWarning;
        directory = Path.Combine(
            recovery,
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N")[..8]);
        CheckParents(Path.Combine(directory, "check"));
        Directory.CreateDirectory(directory);
        journal.Description = description;
        Flush();
    }

    private void Capture(string path)
    {
        EnsureActive();
        path = Path.GetFullPath(path);
        CheckParents(path);

        if (captured.Contains(path))
        {
            return;
        }

        if (Directory.Exists(path))
        {
            throw new IOException("Expected a file, found a directory: " + path);
        }

        var entry = new FileSnapshot
        {
            Path = path,
            LinkTarget = Link(path)
        };
        if (entry.LinkTarget == null && File.Exists(path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Cannot capture an unsupported file reparse point: " + path);
            }
            entry.Backup = journal.Files.Count.ToString("D6") + ".bak";
            File.Copy(path, Path.Combine(directory, entry.Backup));
            using (var backup = new FileStream(Path.Combine(directory, entry.Backup), FileMode.Open, FileAccess.ReadWrite))
            {
                backup.Flush(flushToDisk: true);
            }
            entry.Hash = Hash(Path.Combine(directory, entry.Backup));
        }

        Append(new JournalEvent { File = entry });
        journal.Files.Add(entry);
        captured.Add(path);
    }

    public void WriteText(string path, string text) => WriteBytes(path, System.Text.Encoding.UTF8.GetBytes(text));

    private void WriteBytes(string path, byte[] bytes)
    {
        Capture(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteTemporaryFile(temp, bytes);
            DeleteTargetIfUnchanged(path);
            MoveIntoMissingPath(temp, path);
            RecordMutation(path, FileMutationKind.Contents, Convert.ToHexString(SHA256.HashData(bytes)));
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public void Copy(string source, string target) => Copy(source, target, CancellationToken.None);

    public void Copy(string source, string target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        target = Path.GetFullPath(target);
        Capture(target);
        CopyStaged(source, target, false, cancellationToken);
    }

    internal bool CopyIfUnchanged(string source, string target, string? expectedHash, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (expectedHash == null)
        {
            return CopyIfAbsent(source, target, cancellationToken);
        }

        string fullPath = Path.GetFullPath(target);
        if (captured.Contains(fullPath))
        {
            throw new InvalidOperationException("A validated replacement must be the first change to this file: " + fullPath);
        }

        Capture(fullPath);
        FileSnapshot snapshot = journal.Files.First(item => Same(item.Path, fullPath));
        if (snapshot.Backup == null || !string.Equals(snapshot.Hash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        CopyStaged(source, fullPath, false, cancellationToken);
        return true;
    }

    internal bool WriteTextIfAbsent(string path, string text) =>
        WriteBytesIfAbsent(path, Encoding.UTF8.GetBytes(text));

    internal bool WriteTextIfUnchanged(string path, string? expectedHash, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        if (expectedHash == null)
        {
            return WriteBytesIfAbsent(path, bytes);
        }

        string fullPath = Path.GetFullPath(path);
        if (captured.Contains(fullPath))
        {
            throw new InvalidOperationException("A validated replacement must be the first change to this file: " + fullPath);
        }

        Capture(fullPath);
        FileSnapshot snapshot = journal.Files.First(item => Same(item.Path, fullPath));
        if (snapshot.Backup == null || !string.Equals(snapshot.Hash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        WriteBytes(fullPath, bytes);
        return true;
    }

    internal bool CopyIfAbsent(string source, string target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureActive();
        target = Path.GetFullPath(target);
        CheckParents(target);
        if (Exists(target) || Directory.Exists(target))
        {
            return false;
        }

        Capture(target);
        CopyStaged(source, target, true, cancellationToken);
        return true;
    }

    private void CopyStaged(string source, string target, bool requireAbsent, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[128 * 1024];
                int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, count);
                    hash.AppendData(buffer, 0, count);
                }
                output.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (requireAbsent)
            {
                MoveIntoMissingPath(temp, target);
            }
            else
            {
                DeleteTargetIfUnchanged(target);
                MoveIntoMissingPath(temp, target);
            }
            RecordMutation(target, FileMutationKind.Contents, Convert.ToHexString(hash.GetHashAndReset()));
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    internal bool WriteBytesIfAbsent(string path, byte[] bytes)
    {
        EnsureActive();
        path = Path.GetFullPath(path);
        CheckParents(path);
        if (Exists(path) || Directory.Exists(path))
        {
            return false;
        }

        Capture(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteTemporaryFile(temp, bytes);
            MoveIntoMissingPath(temp, path);
            RecordMutation(path, FileMutationKind.Contents, Convert.ToHexString(SHA256.HashData(bytes)));
            return true;
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public void Delete(string path)
    {
        Capture(path);
        DeleteTargetIfUnchanged(path);
    }

    internal bool DeleteIfUnchanged(string path, string? expectedTarget = null, string? expectedHash = null)
    {
        EnsureActive();
        path = Path.GetFullPath(path);
        CheckParents(path);
        if (captured.Contains(path))
        {
            throw new InvalidOperationException("A validated removal must be the first change to this file: " + path);
        }

        using SafeFileHandle handle = OpenUnregisterFile(path, 0x80010000, FileShare.Read, 0, 3, 0x00200000, 0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            if (error is 2 or 3)
            {
                return false;
            }

            throw new IOException("Cannot open file for safe removal: " + path, new Win32Exception(error));
        }

        CheckParents(path);
        FileAttributes attributes = File.GetAttributes(handle);
        if ((attributes & FileAttributes.Directory) != 0)
        {
            return false;
        }

        var entry = new FileSnapshot { Path = path, RestoreOnlyIfMissing = true };
        bool reparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;
        if (expectedTarget != null)
        {
            entry.LinkTarget = reparsePoint ? ReadUnregisterLink(handle) : null;
            if (entry.LinkTarget == null
                || !Same(Path.GetFullPath(entry.LinkTarget, Path.GetDirectoryName(path)!), expectedTarget))
            {
                return false;
            }

        }
        else if (reparsePoint || expectedHash == null)
        {
            return false;
        }

        using FileStream? contents = expectedTarget == null ? new FileStream(handle, FileAccess.Read) : null;
        if (contents != null)
        {
            entry.Hash = Convert.ToHexString(SHA256.HashData(contents));
            if (entry.Hash != expectedHash)
            {
                return false;
            }
            entry.Backup = journal.Files.Count.ToString("D6") + ".bak";
            contents.Position = 0;
            using var backup = File.Create(Path.Combine(directory, entry.Backup));
            contents.CopyTo(backup);
            backup.Flush(flushToDisk: true);
        }

        Append(new JournalEvent { File = entry });
        journal.Files.Add(entry);
        captured.Add(path);
        byte deleteFile = 1;
        if (!MarkUnregisterFileForDeletion(handle, 4, ref deleteFile, 1))
        {
            throw new IOException(
                "Cannot remove validated file: " + path,
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        return true;
    }

    internal bool RewriteIfUnchanged(string path, string expectedHash, string text)
    {
        EnsureActive();
        path = Path.GetFullPath(path);
        CheckParents(path);
        if (captured.Contains(path))
        {
            throw new InvalidOperationException("A validated rewrite must be the first change to this file: " + path);
        }

        using SafeFileHandle handle = OpenUnregisterFile(path, 0xC0000000, FileShare.Read, 0, 3, 0x00200000, 0);
        if (handle.IsInvalid)
        {
            throw new IOException(
                "Cannot open shared application list: " + path,
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        CheckParents(path);
        if ((File.GetAttributes(handle) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            return false;
        }
        EnsureSingleLink(handle);

        using var contents = new FileStream(handle, FileAccess.ReadWrite);
        string hash = Convert.ToHexString(SHA256.HashData(contents));
        if (hash != expectedHash)
        {
            return false;
        }

        byte[] replacement = Encoding.UTF8.GetBytes(text);
        var entry = new FileSnapshot
        {
            Path = path,
            Backup = journal.Files.Count.ToString("D6") + ".bak",
            Hash = hash,
            ExpectedModifiedHash = Convert.ToHexString(SHA256.HashData(replacement))
        };

        contents.Position = 0;
        using (var backup = File.Create(Path.Combine(directory, entry.Backup)))
        {
            contents.CopyTo(backup);
            backup.Flush(flushToDisk: true);
        }

        Append(new JournalEvent { File = entry });
        journal.Files.Add(entry);
        captured.Add(path);
        try
        {
            contents.Position = 0;
            contents.Write(replacement);
            contents.SetLength(replacement.Length);
            contents.Flush(flushToDisk: true);
        }
        catch
        {
            using var backup = File.OpenRead(Path.Combine(directory, entry.Backup));
            contents.Position = 0;
            backup.CopyTo(contents);
            contents.SetLength(backup.Length);
            contents.Flush(flushToDisk: true);
            throw;
        }

        return true;
    }

    internal static void RestoreValidatedRewrite(FileSnapshot snapshot, string backupPath)
    {
        CheckParents(snapshot.Path);
        using SafeFileHandle handle = OpenUnregisterFile(
            snapshot.Path,
            0xC0000000,
            FileShare.Read,
            0,
            3,
            0x00200000,
            0);
        if (handle.IsInvalid)
        {
            throw new IOException(
                "Cannot safely restore shared application list: " + snapshot.Path,
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        CheckParents(snapshot.Path);
        if ((File.GetAttributes(handle) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new IOException(
                "The shared application list was replaced; recovery data was kept: " + snapshot.Path);
        }
        EnsureSingleLink(handle);

        using var contents = new FileStream(handle, FileAccess.ReadWrite);
        string currentHash = Convert.ToHexString(SHA256.HashData(contents));
        if (currentHash == snapshot.Hash)
        {
            return;
        }

        if (currentHash != snapshot.ExpectedModifiedHash)
        {
            throw new IOException("The shared application list changed; recovery data was kept: " + snapshot.Path);
        }

        if ((File.GetAttributes(backupPath) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException("A recovery backup is a directory or reparse point: " + backupPath);
        }
        using var backup = new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexString(SHA256.HashData(backup)) != snapshot.Hash)
        {
            throw new InvalidDataException("Recovery data changed while it was being restored: " + snapshot.Path);
        }
        backup.Position = 0;
        contents.Position = 0;
        backup.CopyTo(contents);
        contents.SetLength(backup.Length);
        contents.Flush(flushToDisk: true);
    }

    private static string? ReadUnregisterLink(SafeFileHandle handle)
    {
        byte[] data = new byte[16 * 1024];
        if (!ReadUnregisterReparsePoint(handle, 0x000900A8, 0, 0, data, data.Length, out int length, 0))
        {
            throw new IOException(
                "Cannot inspect symbolic link for safe removal.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        if (length < 20 || BitConverter.ToUInt32(data, 0) != 0xA000000C)
        {
            return null;
        }

        int offset = BitConverter.ToUInt16(data, 8);
        int bytes = BitConverter.ToUInt16(data, 10);
        uint flags = BitConverter.ToUInt32(data, 16);
        if (bytes == 0
            || 20 + offset + bytes > length
            || (bytes % 2) != 0
            || (offset % 2) != 0
            || (flags & ~1u) != 0
            || BitConverter.ToUInt16(data, 4) + 8 > length)
        {
            return null;
        }

        string target = Encoding.Unicode.GetString(data, 20 + offset, bytes);
        target = target.StartsWith(@"\??\UNC\", StringComparison.OrdinalIgnoreCase)
            ? @"\\" + target[8..]
            : target.StartsWith(@"\??\", StringComparison.Ordinal)
                ? target[4..]
                : target;

        bool relative = flags == 1;

        return target.Contains('\0') || (relative ? Path.IsPathRooted(target) : !Path.IsPathFullyQualified(target))
            ? null
            : target;
    }

    internal static void DeleteClaimedFile(string path, FileMutation mutation)
    {
        CheckParents(path);
        using SafeFileHandle handle = OpenUnregisterFile(path, 0x80010000, FileShare.Read, 0, 3, 0x00200000, 0);
        if (handle.IsInvalid)
        {
            throw new IOException("Cannot safely remove a transaction file: " + path,
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        CheckParents(path);
        FileAttributes attributes = File.GetAttributes(handle);
        bool reparse = (attributes & FileAttributes.ReparsePoint) != 0;
        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw new IOException("A directory replaced a transaction file: " + path);
        }

        using FileStream? contents = mutation.Kind == FileMutationKind.Contents && !reparse
            ? new FileStream(handle, FileAccess.Read) : null;
        bool matches = mutation.Kind switch
        {
            FileMutationKind.Contents when contents != null => Convert.ToHexString(SHA256.HashData(contents)) == mutation.Value,
            FileMutationKind.SymbolicLink when reparse => ReadUnregisterLink(handle) is string target
                && Same(Path.GetFullPath(target, Path.GetDirectoryName(path)!), mutation.Value!),
            _ => false
        };
        if (!matches)
        {
            throw new IOException("The transaction file changed and was preserved: " + path);
        }

        byte deleteFile = 1;
        if (!MarkUnregisterFileForDeletion(handle, 4, ref deleteFile, 1))
        {
            throw new IOException("Cannot safely remove a transaction file: " + path,
                new Win32Exception(Marshal.GetLastWin32Error()));
        }
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    private static extern SafeFileHandle OpenUnregisterFile(
        string path,
        uint access,
        FileShare share,
        nint security,
        uint disposition,
        uint flags,
        nint template);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadUnregisterReparsePoint(
        SafeFileHandle handle,
        uint control,
        nint input,
        int inputSize,
        [Out] byte[] output,
        int outputSize,
        out int returned,
        nint overlapped);

    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MarkUnregisterFileForDeletion(
        SafeFileHandle handle,
        int informationClass,
        ref byte information,
        int size);

    public void Symlink(string path, string target)
    {
        if (!File.Exists(target))
        {
            throw new FileNotFoundException("Symlink target is missing.", target);
        }

        Capture(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        DeleteTargetIfUnchanged(path);
        File.CreateSymbolicLink(path, Path.GetFullPath(target));
        RecordMutation(path, FileMutationKind.SymbolicLink, Path.GetFullPath(target));
        if (Link(path) == null || !Same(new FileInfo(path).ResolveLinkTarget(true)!.FullName, target))
        {
            throw new IOException("Could not verify symbolic link: " + path);
        }
    }

    internal void SymlinkIfAbsent(string path, string target)
    {
        if (!File.Exists(target))
        {
            throw new FileNotFoundException("Symlink target is missing.", target);
        }

        path = Path.GetFullPath(path);
        Capture(path);
        if (Exists(path) || Directory.Exists(path))
        {
            throw new IOException("A file already occupies the link path: " + path);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string absoluteTarget = Path.GetFullPath(target);
        File.CreateSymbolicLink(path, absoluteTarget);
        RecordMutation(path, FileMutationKind.SymbolicLink, absoluteTarget);
        if (Link(path) == null || !Same(new FileInfo(path).ResolveLinkTarget(true)!.FullName, target))
        {
            throw new IOException("Could not verify symbolic link: " + path);
        }
    }

    public void SetRegistry(LayerRegistration key, int? value)
    {
        EnsureActive();
        if (registry == null)
        {
            throw new InvalidOperationException("No registry backend.");
        }

        if (!journal.Registry.Any(snapshot => snapshot.Registration == key))
        {
            var snapshot = new RegistrySnapshot(key, registry.Read(key));
            Append(new JournalEvent { Registry = snapshot });
            journal.Registry.Add(snapshot);
        }

        RegistryMutation? last = journal.RegistryMutations.LastOrDefault(item => item.Registration == key);
        int? expected = last == null
            ? journal.Registry.First(item => item.Registration == key).PreviousValue
            : last.Value;
        if (registry.Read(key) != expected)
        {
            throw new IOException("The Vulkan registry value changed during this operation: " + key.Manifest);
        }

        registry.Write(key, value);
        var mutation = new RegistryMutation(key, value);
        Append(new JournalEvent { RegistryMutation = mutation });
        journal.RegistryMutations.Add(mutation);
    }

    public void Commit()
    {
        EnsureActive();
        journal.Status = "committed";
        Flush();
        done = true;
        TransactionRecovery.Cleanup(directory, reportWarning);
    }

    private void EnsureActive()
    {
        if (done)
        {
            throw new InvalidOperationException("This file transaction has already completed.");
        }
    }

    private void RecordMutation(string path, FileMutationKind kind, string? value)
    {
        var mutation = new FileMutation { Path = Path.GetFullPath(path), Kind = kind, Value = value };
        Append(new JournalEvent { Mutation = mutation });
        journal.Mutations.Add(mutation);
    }

    private static void WriteTemporaryFile(string path, byte[] bytes)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }

    private void EnsureTargetUnchanged(string path)
    {
        path = Path.GetFullPath(path);
        CheckParents(path);
        FileMutation? last = journal.Mutations.LastOrDefault(item => Same(item.Path, path));
        FileSnapshot snapshot = journal.Files.First(item => Same(item.Path, path));
        if (last == null ? !TransactionRecovery.AlreadyOriginal(snapshot) : !TransactionRecovery.MatchesMutation(path, last))
        {
            throw new IOException("The file changed during this operation and was preserved: " + path);
        }
    }

    private void DeleteTargetIfUnchanged(string path)
    {
        path = Path.GetFullPath(path);
        EnsureTargetUnchanged(path);
        if (!Exists(path))
        {
            return;
        }

        FileSnapshot snapshot = journal.Files.First(item => Same(item.Path, path));
        FileMutation? mutation = journal.Mutations.LastOrDefault(item => Same(item.Path, path));
        mutation ??= snapshot.LinkTarget != null
            ? new FileMutation
            {
                Path = path,
                Kind = FileMutationKind.SymbolicLink,
                Value = Path.GetFullPath(snapshot.LinkTarget, Path.GetDirectoryName(path)!)
            }
            : new FileMutation { Path = path, Kind = FileMutationKind.Contents, Value = snapshot.Hash };
        DeleteClaimedFile(path, mutation);
        RecordMutation(path, FileMutationKind.Missing, null);
    }

    internal static void MoveIntoMissingPath(string source, string target)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(source, target, false);
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                int code = error.HResult & 0xffff;
                if (attempt >= 5 || code is not (5 or 32 or 33))
                {
                    throw;
                }
                Thread.Sleep(25 << attempt);
            }
        }
    }

    private void Append(JournalEvent entry)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, CompactJson) + "\n");
        string path = Path.Combine(directory, "events.jsonl");
        CheckTransactionFile(path);
        using var events = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        events.Write(bytes);
        events.Flush(flushToDisk: true);
    }

    private void Flush()
    {
        WriteJournal(Path.Combine(directory, "transaction.json"), journal);
    }

    internal static void WriteJournal(string path, TransactionJournal journal)
    {
        CheckTransactionFile(path);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteTemporaryFile(temp, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(journal, ManagerState.Json)));
            AtomicMove(temp, path);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static void CheckTransactionFile(string path)
    {
        CheckParents(path);
        if ((Exists(path) || Directory.Exists(path))
            && (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new IOException("A transaction file is a directory or reparse point: " + path);
        }
    }

    public void Dispose()
    {
        if (done)
        {
            return;
        }

        TransactionRecovery.Rollback(directory, registry);
        done = true;
        TransactionRecovery.Cleanup(directory, reportWarning);
    }
}
