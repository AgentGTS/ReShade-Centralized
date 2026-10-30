using System.Diagnostics;
using System.Text.Json;
using static ReShadeCentralized.LocalFiles;

namespace ReShadeCentralized;

public static class TransactionRecovery
{
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
                    JournalEvent entry = JsonSerializer.Deserialize<JournalEvent>(line, ManagerState.Json) ??
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

        foreach (FileSnapshot item in journal.Files.Where(snapshot => snapshot.Backup != null))
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
        foreach (FileSnapshot item in restore)
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

        foreach (FileSnapshot item in restore)
        {
            CheckParents(item.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(item.Path)!);
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

        foreach (RegistrySnapshot item in journal.Registry.AsEnumerable().Reverse())
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

    internal static void Cleanup(string directory, Action<string>? reportWarning)
    {
        try
        {
            // Only remove the specific transaction directory, never its parent or a linked tree.
            CheckParents(Path.Combine(directory, "check"));
            if (Directory.EnumerateDirectories(directory).Any())
            {
                throw new IOException("The recovery folder contains unexpected subfolders.");
            }

            string journalPath = Path.Combine(directory, "transaction.json");
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                if (!Same(file, journalPath))
                {
                    File.Delete(file);
                }
            }

            // Keep the completion record until all payloads are gone, including when cleanup is interrupted.
            File.Delete(journalPath);
            Directory.Delete(directory);
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
        if (!Directory.Exists(recovery))
        {
            return;
        }

        foreach (string directory in Directory.GetDirectories(recovery).OrderDescending())
        {
            if (!File.Exists(Path.Combine(directory, "transaction.json")) &&
                !Directory.EnumerateFileSystemEntries(directory).Any())
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
