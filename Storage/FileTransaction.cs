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
            entry.Backup = journal.Files.Count.ToString("D6") + ".bak";
            File.Copy(path, Path.Combine(directory, entry.Backup));
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
            File.WriteAllBytes(temp, bytes);
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

    public void Copy(string source, string target) => WriteBytes(target, File.ReadAllBytes(source));

    public void Delete(string path)
    {
        Capture(path);
        if (Exists(path))
        {
            File.Delete(path);
        }
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

        using var backup = File.OpenRead(backupPath);
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
        if (Exists(path))
        {
            File.Delete(path);
        }

        File.CreateSymbolicLink(path, Path.GetFullPath(target));
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

        registry.Write(key, value);
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

    private void Append(JournalEvent entry) =>
        File.AppendAllText(
            Path.Combine(directory, "events.jsonl"),
            JsonSerializer.Serialize(entry, CompactJson) + "\n");

    private void Flush()
    {
        string path = Path.Combine(directory, "transaction.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(journal, ManagerState.Json));
        AtomicMove(path + ".tmp", path);
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
