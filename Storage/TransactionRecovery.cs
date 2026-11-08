using System.Diagnostics;
using System.Text.Json;
using static ReShadeCentralized.LocalFiles;

namespace ReShadeCentralized;

public static class TransactionRecovery
{
    private const long MaximumJournalBytes = 256L * 1024 * 1024;

    public static TransactionJournal ReadJournal(string directory)
    {
        CheckParents(Path.Combine(directory, "check"));
        string journalPath = Path.Combine(directory, "transaction.json");
        CheckRecoveryFile(journalPath);
        string document = ReadRecoveryText(journalPath);
        using (JsonDocument parsed = JsonDocument.Parse(document))
        {
            if (parsed.RootElement.ValueKind != JsonValueKind.Object
                || !parsed.RootElement.TryGetProperty("Status", out JsonElement status)
                || status.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("The recovery journal has no valid status.");
            }
        }
        TransactionJournal journal = JsonSerializer.Deserialize<TransactionJournal>(
            document,
            ManagerState.Json) ??
            throw new InvalidDataException("Invalid recovery journal.");
        if (journal.Status is not ("pending" or "committed" or "restored")
            || journal.Files == null || journal.Mutations == null || journal.Registry == null
            || journal.RegistryMutations == null)
        {
            throw new InvalidDataException("The recovery journal has an invalid status or contents.");
        }
        string events = Path.Combine(directory, "events.jsonl");
        if (journal.Status == "pending" && (Exists(events) || Directory.Exists(events)))
        {
            CheckRecoveryFile(events);
            string text = ReadRecoveryText(events);
            int end = text.LastIndexOf('\n');
            if (end >= 0)
            {
                foreach (string line in text[..end].Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    JournalEvent entry = JsonSerializer.Deserialize<JournalEvent>(line, ManagerState.Json)
                        ?? throw new InvalidDataException("Invalid transaction event.");
                    if ((entry.File != null ? 1 : 0) + (entry.Registry != null ? 1 : 0)
                        + (entry.Mutation != null ? 1 : 0) + (entry.RegistryMutation != null ? 1 : 0) != 1)
                    {
                        throw new InvalidDataException("A transaction event has invalid contents.");
                    }
                    if (entry.File != null)
                    {
                        journal.Files.Add(entry.File);
                    }

                    if (entry.Registry != null)
                    {
                        journal.Registry.Add(entry.Registry);
                    }
                    if (entry.Mutation != null)
                    {
                        journal.Mutations.Add(entry.Mutation);
                    }
                    if (entry.RegistryMutation != null)
                    {
                        journal.RegistryMutations.Add(entry.RegistryMutation);
                    }
                }
            }
        }

        ValidateJournal(journal);
        return journal;
    }

    private static void CheckRecoveryFile(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException("A recovery file is a directory or reparse point: " + path);
        }
    }

    private static string ReadRecoveryText(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaximumJournalBytes)
        {
            throw new InvalidDataException("A recovery journal exceeds the supported size: " + path);
        }

        using var reader = new StreamReader(file);
        return reader.ReadToEnd();
    }

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static void ValidateJournal(TransactionJournal journal)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var backups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FileSnapshot item in journal.Files)
        {
            if (item == null || !IsSafeAbsolutePath(item.Path) || !paths.Add(Path.GetFullPath(item.Path)))
            {
                throw new InvalidDataException("A recovery file path is invalid or duplicated.");
            }

            if (item.Backup != null && (!IsBackupName(item.Backup) || !backups.Add(item.Backup) || !IsHash(item.Hash))
                || item.Backup == null && item.Hash != null
                || item.LinkTarget != null && (item.Backup != null || item.Hash != null)
                || item.ExpectedModifiedHash != null && (item.Backup == null || !IsHash(item.ExpectedModifiedHash)))
            {
                throw new InvalidDataException("A recovery file record is invalid: " + item.Path);
            }
        }

        foreach (FileMutation mutation in journal.Mutations)
        {
            if (mutation == null || !IsSafeAbsolutePath(mutation.Path) || !paths.Contains(Path.GetFullPath(mutation.Path))
                || mutation.Kind switch
                {
                    FileMutationKind.Missing => mutation.Value != null,
                    FileMutationKind.Contents => !IsHash(mutation.Value),
                    FileMutationKind.SymbolicLink => mutation.Value == null || !IsSafeAbsolutePath(mutation.Value),
                    _ => true
                })
            {
                throw new InvalidDataException("A recovery mutation record is invalid.");
            }
        }

        var registrations = new HashSet<LayerRegistration>();
        foreach (RegistrySnapshot item in journal.Registry)
        {
            if (item == null || !IsValidRegistration(item.Registration) || !registrations.Add(item.Registration))
            {
                throw new InvalidDataException("A recovery registry record is invalid or duplicated.");
            }
        }

        if (journal.RegistryMutations.Any(item => item == null || !IsValidRegistration(item.Registration)
            || !registrations.Contains(item.Registration)))
        {
            throw new InvalidDataException("A recovery registry mutation record is invalid.");
        }
    }

    private static bool IsSafeAbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            string root = Path.GetPathRoot(path)!;
            return Same(Contained(root, path[root.Length..]), path);
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsBackupName(string name) => name.EndsWith(".bak", StringComparison.Ordinal)
        && name.Length is >= 10 and <= 14
        && name.AsSpan(0, name.Length - 4).IndexOfAnyExceptInRange('0', '9') < 0;

    private static bool IsValidRegistration(LayerRegistration? registration) => registration != null
        && registration.Hive is "HKCU" or "HKLM"
        && registration.View is "32" or "64"
        && registration.Kind is "Implicit" or "Explicit"
        && !string.IsNullOrWhiteSpace(registration.Manifest);

    public static void Rollback(string directory, ILayerRegistry? registry)
    {
        TransactionJournal journal = ReadJournal(directory);
        if (journal.Status == "restored")
        {
            return;
        }
        if (journal.Status != "pending")
        {
            throw new InvalidDataException("Only a pending transaction can be rolled back.");
        }

        string journalPath = Path.Combine(directory, "transaction.json");
        if (Exists(journalPath + ".tmp") || Directory.Exists(journalPath + ".tmp"))
        {
            CheckRecoveryFile(journalPath + ".tmp");
        }

        foreach (FileSnapshot item in journal.Files.Where(snapshot => snapshot.Backup != null))
        {
            string backup = Contained(directory, item.Backup!);
            CheckRecoveryFile(backup);
            if (Hash(backup) != item.Hash)
            {
                throw new InvalidDataException("Recovery data hash mismatch: " + item.Path);
            }
        }

        if (journal.Files.Any(item => item.ExpectedModifiedHash != null && (item.Backup == null || item.Hash == null)))
        {
            throw new InvalidDataException("A validated shared-file recovery record is incomplete.");
        }

        if (journal.Registry.Count > 0 && registry == null)
        {
            throw new InvalidOperationException("Registry access is required to recover this operation.");
        }

        foreach (RegistrySnapshot item in journal.Registry)
        {
            EnsureRegistryCanRestore(item, journal, registry!);
        }

        var restore = new List<FileSnapshot>();
        foreach (FileSnapshot item in journal.Files.AsEnumerable().Reverse())
        {
            CheckParents(item.Path);
            if (AlreadyOriginal(item))
            {
                continue;
            }

            EnsureFileCanRestore(item, journal, directory);

            if (File.Exists(item.Path) && Link(item.Path) == null)
            {
                using var handle = new FileStream(
                    item.Path,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete);
            }

            restore.Add(item);
        }

        foreach (FileSnapshot item in restore)
        {
            CheckParents(item.Path);
            if (AlreadyOriginal(item))
            {
                continue;
            }
            EnsureFileCanRestore(item, journal, directory);
            Directory.CreateDirectory(Path.GetDirectoryName(item.Path)!);
            if (item.ExpectedModifiedHash != null)
            {
                FileTransaction.RestoreValidatedRewrite(item, Contained(directory, item.Backup!));
                continue;
            }

            if (item.LinkTarget != null)
            {
                if (Exists(item.Path))
                {
                    DeleteClaimedFile(item, journal);
                }

                File.CreateSymbolicLink(item.Path, item.LinkTarget);
            }
            else if (item.Backup != null)
            {
                RestoreBackup(item, journal, directory);
            }
            else if (Exists(item.Path))
            {
                DeleteClaimedFile(item, journal);
            }
        }

        foreach (RegistrySnapshot item in journal.Registry.AsEnumerable().Reverse())
        {
            EnsureRegistryCanRestore(item, journal, registry!);
            if (registry!.Read(item.Registration) != item.PreviousValue)
            {
                registry.Write(item.Registration, item.PreviousValue);
            }
        }

        journal.Status = "restored";
        if (Exists(journalPath + ".tmp") || Directory.Exists(journalPath + ".tmp"))
        {
            CheckRecoveryFile(journalPath + ".tmp");
        }
        FileTransaction.WriteJournal(journalPath, journal);
    }

    internal static bool AlreadyOriginal(FileSnapshot item)
    {
        if (item.LinkTarget != null)
        {
            return Link(item.Path) == item.LinkTarget;
        }

        if (item.Backup != null)
        {
            return IsRegularFile(item.Path)
                && Hash(item.Path) == item.Hash;
        }

        return !Exists(item.Path) && !Directory.Exists(item.Path);
    }

    private static bool IsRegularFile(string path) => File.Exists(path)
        && (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;

    private static void EnsureFileCanRestore(FileSnapshot snapshot, TransactionJournal journal, string directory)
    {
        bool missing = !Exists(snapshot.Path) && !Directory.Exists(snapshot.Path);
        bool expectedRewrite = snapshot.ExpectedModifiedHash != null
            && IsRegularFile(snapshot.Path) && Hash(snapshot.Path) == snapshot.ExpectedModifiedHash;
        bool intended = journal.Mutations.Where(item => Same(item.Path, snapshot.Path))
            .Any(item => MatchesMutation(snapshot.Path, item));
        if (!expectedRewrite && !missing && !intended)
        {
            throw new IOException("Cannot restore " + snapshot.Path +
                ": it changed outside the app or its expected state is unknown. It was preserved; " +
                "recovery data is retained in " + directory + ".");
        }
    }

    internal static bool MatchesMutation(string path, FileMutation mutation)
    {
        return mutation.Kind switch
        {
            FileMutationKind.Missing => !Exists(path) && !Directory.Exists(path),
            FileMutationKind.Contents => IsRegularFile(path) && Hash(path) == mutation.Value,
            FileMutationKind.SymbolicLink => Link(path) is string target
                && Same(Path.GetFullPath(target, Path.GetDirectoryName(path)!), mutation.Value!),
            _ => false
        };
    }

    private static void EnsureRegistryCanRestore(RegistrySnapshot snapshot, TransactionJournal journal, ILayerRegistry registry)
    {
        int? current = registry.Read(snapshot.Registration);
        if (current != snapshot.PreviousValue
            && !journal.RegistryMutations.Any(item => item.Registration == snapshot.Registration && item.Value == current))
        {
            throw new IOException("A Vulkan registry value changed outside the app; recovery data was retained: " +
                snapshot.Registration.Manifest);
        }
    }

    private static void DeleteClaimedFile(FileSnapshot snapshot, TransactionJournal journal)
    {
        FileMutation mutation = journal.Mutations.LastOrDefault(item => Same(item.Path, snapshot.Path)
            && MatchesMutation(snapshot.Path, item))
            ?? throw new IOException("The transaction file changed and was preserved: " + snapshot.Path);
        FileTransaction.DeleteClaimedFile(snapshot.Path, mutation);
    }

    private static void RestoreBackup(FileSnapshot snapshot, TransactionJournal journal, string directory)
    {
        string backup = Contained(directory, snapshot.Backup!);
        CheckRecoveryFile(backup);
        string temp = snapshot.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(backup, temp, false);
            using (var file = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                file.Flush(flushToDisk: true);
            }
            if (Hash(temp) != snapshot.Hash)
            {
                throw new InvalidDataException("Recovery data changed while it was being restored: " + snapshot.Path);
            }

            CheckParents(snapshot.Path);
            if (AlreadyOriginal(snapshot))
            {
                return;
            }
            EnsureFileCanRestore(snapshot, journal, directory);
            if (Exists(snapshot.Path))
            {
                DeleteClaimedFile(snapshot, journal);
            }
            FileTransaction.MoveIntoMissingPath(temp, snapshot.Path);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    internal static void Cleanup(string directory, Action<string>? reportWarning)
    {
        try
        {
            CheckParents(Path.Combine(directory, "check"));
            if (Directory.EnumerateDirectories(directory).Any())
            {
                throw new IOException("The recovery folder contains unexpected subfolders.");
            }

            string journalPath = Path.Combine(directory, "transaction.json");
            string[] files = Directory.GetFiles(directory);
            if (files.Length == 0)
            {
                Directory.Delete(directory);
                return;
            }

            TransactionJournal journal = ReadJournal(directory);
            if (journal.Status is not ("committed" or "restored"))
            {
                throw new IOException("An unfinished recovery folder cannot be removed.");
            }

            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "transaction.json", "transaction.json.tmp", "events.jsonl"
            };
            foreach (FileSnapshot snapshot in journal.Files)
            {
                if (snapshot.Backup != null)
                {
                    allowed.Add(snapshot.Backup);
                }
            }

            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                if (!allowed.Contains(Path.GetFileName(file)))
                {
                    throw new IOException("The recovery folder contains an unexpected file: " + file);
                }
                CheckRecoveryFile(file);
                hashes[file] = Hash(file);
            }
            foreach (FileSnapshot snapshot in journal.Files.Where(item => item.Backup != null))
            {
                string backup = Contained(directory, snapshot.Backup!);
                if (hashes.TryGetValue(backup, out string? actual) && actual != snapshot.Hash)
                {
                    throw new InvalidDataException("Recovery data changed and was preserved: " + backup);
                }
            }

            if (!hashes.TryGetValue(journalPath, out string? journalHash))
            {
                throw new IOException("The recovery journal disappeared before cleanup: " + journalPath);
            }

            foreach (string file in files.Where(file => !Same(file, journalPath)))
            {
                FileTransaction.DeleteClaimedFile(file,
                    new FileMutation { Path = file, Kind = FileMutationKind.Contents, Value = hashes[file] });
            }
            FileTransaction.DeleteClaimedFile(journalPath,
                new FileMutation { Path = journalPath, Kind = FileMutationKind.Contents, Value = journalHash });
            Directory.Delete(directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            string message = "Could not remove temporary recovery data: " + directory + ". " + error.Message;
            ReportWarning(reportWarning, message);
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

        CheckParents(Path.Combine(recovery, "check"));
        foreach (string directory in Directory.GetDirectories(recovery).OrderDescending())
        {
            CheckParents(Path.Combine(directory, "check"));
            if (!IsTransactionDirectory(directory))
            {
                string message = "Unrecognized recovery folder was preserved: " + directory;
                ReportWarning(reportWarning, message);
                continue;
            }

            string journalPath = Path.Combine(directory, "transaction.json");
            if (!Exists(journalPath) && !Directory.Exists(journalPath))
            {
                string[] entries = Directory.GetFileSystemEntries(directory);
                if (entries.Length == 0)
                {
                    Cleanup(directory, reportWarning);
                    continue;
                }

                if (entries.All(IsInitializationTemporary))
                {
                    foreach (string file in entries)
                    {
                        CheckRecoveryFile(file);
                    }
                    string message = "An incomplete recovery initialization was preserved: " + directory;
                    ReportWarning(reportWarning, message);
                    continue;
                }
            }

            if (ReadJournal(directory).Status == "pending")
            {
                Rollback(directory, registry);
            }

            Cleanup(directory, reportWarning);
        }
    }

    private static bool IsInitializationTemporary(string path)
    {
        string name = Path.GetFileName(path);
        const string prefix = "transaction.json.";
        const string suffix = ".tmp";
        if (name == "transaction.json.tmp")
        {
            return true;
        }

        return name.StartsWith(prefix, StringComparison.Ordinal)
            && name.EndsWith(suffix, StringComparison.Ordinal)
            && name.Length == prefix.Length + 32 + suffix.Length
            && name.AsSpan(prefix.Length, 32).ToString().All(Uri.IsHexDigit);
    }

    private static void ReportWarning(Action<string>? reportWarning, string message)
    {
        Trace.TraceWarning(message);
        try
        {
            reportWarning?.Invoke(message);
        }
        catch (Exception error)
        {
            Trace.TraceWarning("Could not report recovery warning: {0}", error.Message);
        }
    }

    private static bool IsTransactionDirectory(string directory)
    {
        string name = Path.GetFileName(directory);
        return name.Length == 32 && name[8] == '-' && name[15] == '-' && name[23] == '-'
            && name.AsSpan(0, 8).IndexOfAnyExceptInRange('0', '9') < 0
            && name.AsSpan(9, 6).IndexOfAnyExceptInRange('0', '9') < 0
            && name.AsSpan(16, 7).IndexOfAnyExceptInRange('0', '9') < 0
            && name.AsSpan(24, 8).ToString().All(Uri.IsHexDigit);
    }
}
