using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace ReShadeCentralized;

public sealed class FileSnapshot
{
    public string Path { get; set; } = "";
    public string? Backup { get; set; }

    public string? Hash { get; set; }

    public string? LinkTarget { get; set; }
}

public sealed class TransactionJournal
{
    public string Description { get; set; } = "";
    public string Status { get; set; } = "pending";
    public List<FileSnapshot> Files { get; set; } = [];
    public List<RegistrySnapshot> Registry { get; set; } = [];
}

public sealed class JournalEvent
{
    public FileSnapshot? File { get; set; }

    public RegistrySnapshot? Registry { get; set; }
}

// Recovery data lasts only until an operation commits or rolls back.
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
    public string RecoveryDirectory => directory;

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
        System.IO.Directory.CreateDirectory(directory);
        journal.Description = description;
        Flush();
    }

    public static string Hash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static string? Link(string path) => new FileInfo(path).LinkTarget;

    public static bool Exists(string path) => File.Exists(path) || Link(path) != null;

    public static bool Same(string firstPath, string secondPath)
    {
        string normalizedFirst = Path.GetFullPath(firstPath).TrimEnd(Path.DirectorySeparatorChar);
        string normalizedSecond = Path.GetFullPath(secondPath).TrimEnd(Path.DirectorySeparatorChar);
        return normalizedFirst.Equals(normalizedSecond, StringComparison.OrdinalIgnoreCase);
    }

    public static string Contained(string root, string relative)
    {
        if (Path.IsPathRooted(relative))
        {
            throw new InvalidDataException("Absolute archive/file entry rejected.");
        }

        string path = Path.GetFullPath(Path.Combine(root, relative));
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Path escapes its destination.");
        }

        return path;
    }

    public static void CheckParents(string path)
    {
        for (DirectoryInfo? parent = System.IO.Directory.GetParent(Path.GetFullPath(path));
            parent != null;
            parent = parent.Parent)
        {
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("A destination parent is a reparse point: " + parent.FullName);
            }
        }
    }

    public void Capture(string path)
    {
        path = Path.GetFullPath(path);
        CheckParents(path);
        if (captured.Contains(path))
        {
            return;
        }

        if (System.IO.Directory.Exists(path))
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

    public void WriteBytes(string path, byte[] bytes)
    {
        Capture(path);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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

    public void Symlink(string path, string target)
    {
        if (!File.Exists(target))
        {
            throw new FileNotFoundException("Symlink target is missing.", target);
        }

        Capture(path);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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
        journal.Status = "committed";
        Flush();
        done = true;
        Cleanup(directory, reportWarning);
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

    private static void AtomicMove(string source, string target)
    {
        // Virus scanners can briefly hold newly downloaded files and frequently written
        // journals. Bounded retries also leave genuinely locked game DLLs recoverable.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(source, target, true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                int code = e.HResult & 0xffff;
                if (attempt >= 5 || code is not (5 or 32 or 33))
                {
                    throw new IOException(
                        "Could not replace " + target +
                        ". Close affected games and check write permissions.",
                        e);
                }

                Thread.Sleep(25 << attempt);
            }
        }
    }

    public void Dispose()
    {
        if (done)
        {
            return;
        }

        Rollback(directory, registry);
        done = true;
        Cleanup(directory, reportWarning);
    }

    public static TransactionJournal ReadJournal(string directory)
    {
        TransactionJournal journal = JsonSerializer.Deserialize<TransactionJournal>(
            File.ReadAllText(Path.Combine(directory, "transaction.json")),
            ManagerState.Json) ??
            throw new InvalidDataException("Invalid recovery journal.");
        string events = Path.Combine(directory, "events.jsonl");
        if (journal.Status == "pending" && File.Exists(events))
        {
            string text = File.ReadAllText(events);
            // A crash during append happens before the corresponding mutation. Ignore
            // only an unterminated final record; completed records must all be valid.
            int end = text.LastIndexOf('\n');
            if (end >= 0)
            {
                foreach (string line in text[..end].Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    JournalEvent entry = JsonSerializer.Deserialize<JournalEvent>(line, CompactJson) ??
                        throw new InvalidDataException("Invalid transaction event.");
                    if (entry.File != null)
                    {
                        journal.Files.Add(entry.File);
                    }

                    if (entry.Registry != null)
                    {
                        journal.Registry.Add(entry.Registry);
                    }

                }
            }
        }

        return journal;
    }

    public static void Rollback(string directory, ILayerRegistry? registry)
    {
        TransactionJournal journal = ReadJournal(directory);
        if (journal.Status == "restored")
        {
            return;
        }

        foreach (FileSnapshot? item in journal.Files.Where(snapshot => snapshot.Backup != null))
        {
            if (Hash(Contained(directory, item.Backup!)) != item.Hash)
            {
                throw new InvalidDataException("Recovery data hash mismatch: " + item.Path);
            }
        }

        if (journal.Registry.Count > 0 && registry == null)
        {
            throw new InvalidOperationException("Registry access is required to recover this operation.");
        }

        FileSnapshot[] restore = journal.Files.AsEnumerable().Reverse().Where(item => !AlreadyOriginal(item)).ToArray();
        // Check locked/read-only regular destinations before undoing any other file.
        foreach (FileSnapshot? item in restore)
        {
            CheckParents(item.Path);
            if (File.Exists(item.Path) && Link(item.Path) == null)
            {
                using var handle = new FileStream(
                    item.Path,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete);
            }
        }

        foreach (FileSnapshot? item in restore)
        {
            CheckParents(item.Path);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(item.Path)!);
            if (item.LinkTarget != null)
            {
                if (Exists(item.Path))
                {
                    File.Delete(item.Path);
                }

                File.CreateSymbolicLink(item.Path, item.LinkTarget);
            }
            else if (item.Backup != null)
            {
                if (Link(item.Path) != null)
                {
                    File.Delete(item.Path);
                }

                File.Copy(Contained(directory, item.Backup), item.Path, true);
            }
            else if (Exists(item.Path))
            {
                File.Delete(item.Path);
            }
        }

        foreach (RegistrySnapshot? item in journal.Registry.AsEnumerable().Reverse())
        {
            registry!.Write(item.Registration, item.PreviousValue);
        }

        journal.Status = "restored";
        string journalPath = Path.Combine(directory, "transaction.json");
        File.WriteAllText(
            journalPath + ".tmp",
            JsonSerializer.Serialize(journal, ManagerState.Json));
        AtomicMove(journalPath + ".tmp", journalPath);
    }

    private static bool AlreadyOriginal(FileSnapshot item)
    {
        if (item.LinkTarget != null)
        {
            return Link(item.Path) == item.LinkTarget;
        }

        if (item.Backup != null)
        {
            return Link(item.Path) == null && File.Exists(item.Path) && Hash(item.Path) == item.Hash;
        }

        return !Exists(item.Path);
    }

    private static void Cleanup(string directory, Action<string>? reportWarning)
    {
        try
        {
            // Only remove the specific transaction directory, never its parent or a linked tree.
            CheckParents(Path.Combine(directory, "check"));
            if (System.IO.Directory.EnumerateDirectories(directory).Any())
            {
                throw new IOException("The recovery folder contains unexpected subfolders.");
            }

            string journalPath = Path.Combine(directory, "transaction.json");
            foreach (string file in System.IO.Directory.EnumerateFiles(directory))
            {
                if (!Same(file, journalPath))
                {
                    File.Delete(file);
                }
            }

            // Keep the completion record until all payloads are gone, including when cleanup is interrupted.
            File.Delete(journalPath);
            System.IO.Directory.Delete(directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            string message = "Could not remove temporary recovery data: " + directory + ". " + error.Message;
            Trace.TraceWarning(message);
            reportWarning?.Invoke(message);
        }
    }

    public static void RecoverPending(
        string recovery,
        ILayerRegistry registry,
        Action<string>? reportWarning = null)
    {
        if (!System.IO.Directory.Exists(recovery))
        {
            return;
        }

        foreach (string directory in System.IO.Directory.GetDirectories(recovery).OrderDescending())
        {
            if (!File.Exists(Path.Combine(directory, "transaction.json")) &&
                !System.IO.Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Cleanup(directory, reportWarning);

                continue;
            }

            if (ReadJournal(directory).Status == "pending")
            {
                Rollback(directory, registry);
            }

            Cleanup(directory, reportWarning);
        }
    }
}
