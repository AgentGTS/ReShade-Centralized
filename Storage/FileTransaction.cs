using System.Text.Json;
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
        TransactionRecovery.Cleanup(directory, reportWarning);
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
