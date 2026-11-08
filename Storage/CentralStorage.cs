namespace ReShadeCentralized;

public sealed class CentralStorage(StateStore store, ILayerRegistry registry) : IDisposable
{
    private readonly SemaphoreSlim operationGate = new(1, 1);

    public StateStore Store { get; } = store;
    public ManagerState? State { get; private set; }
    public event Action<string>? Warning;

    public ManagerState RequireState() => State ??
        throw new InvalidOperationException("Choose central storage in Setup first.");

    internal void ReportWarning(string message)
    {
        try
        {
            Warning?.Invoke(message);
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceWarning("Could not display a storage warning: {0}. {1}", message, error.Message);
        }
    }

    internal FileTransaction BeginTransaction(string description)
    {
        TransactionRecovery.RecoverPending(Store.Recovery, registry, reportWarning: ReportWarning);
        return new(Store.Recovery, description, registry, ReportWarning);
    }

    internal void Commit(ManagerState state, FileTransaction transaction, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string savedHash = Store.Save(state, transaction);
        transaction.Commit();
        Store.AcceptSavedState(savedHash);
        State = state;
    }

    public void Open()
    {
        if (!operationGate.Wait(0))
        {
            throw new IOException("Central storage is already in use by this app instance.");
        }

        try
        {
            Store.ReadSelection();
            if (!Store.IsSelected)
            {
                return;
            }

            using StorageLease lease = StorageLease.Acquire(Store.DirectoryPath);
            TransactionRecovery.RecoverPending(Store.Recovery, registry, reportWarning: ReportWarning);
            State = LoadStorage();
            if (State == null && Store.PointerPath != null)
            {
                throw new InvalidDataException("Saved central storage has no registration state: " +
                    Store.DirectoryPath + ". Select the existing central folder in Setup.");
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    private ManagerState? LoadStorage(CancellationToken cancellationToken = default)
    {
        ManagerState? state = Store.Load(out IReadOnlyList<string> repairs);
        if (state == null || repairs.Count == 0)
        {
            return state;
        }

        using FileTransaction transaction = BeginTransaction("Migrate / repair central storage settings");
        cancellationToken.ThrowIfCancellationRequested();
        string backup = LocalFiles.Contained(Store.DirectoryPath,
            Path.Combine("Backups", "settings-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" +
                Guid.NewGuid().ToString("N") + ".json"));
        if (!transaction.CopyIfAbsent(Store.StatePath, backup))
        {
            throw new IOException("Cannot preserve the original settings before migration: " + backup);
        }
        string savedHash = Store.Save(state, transaction);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        Store.AcceptSavedState(savedHash);
        ReportWarning("Original storage settings were backed up before migration: " + backup);
        foreach (string repair in repairs)
        {
            ReportWarning(repair);
        }

        return state;
    }

    internal async Task RunAsync(Func<Task> operation, CancellationToken cancellationToken, bool acquireLease = true)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            await Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using StorageLease? lease = acquireLease ? StorageLease.Acquire(Store.DirectoryPath) : null;
                await operation();
            }, cancellationToken);
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal async Task ChangeAsync(
        string description,
        Func<ManagerState, FileTransaction, Task> action,
        CancellationToken cancellationToken)
    {
        await RunAsync(
            async () =>
            {
                ManagerState updatedState = RequireState().Clone();
                using FileTransaction transaction = BeginTransaction(description);
                await action(updatedState, transaction);
                Commit(updatedState, transaction, cancellationToken);
            },
            cancellationToken);
    }

    public async Task InitializeAsync(string root, CancellationToken cancellationToken)
    {
        await RunAsync(() =>
        {
            if (State != null)
            {
                throw new InvalidOperationException("A central folder is already configured. " +
                    "Changing storage requires a fresh beta setup.");
            }

            string? previousDirectory = Store.IsSelected ? Store.DirectoryPath : null;
            try
            {
                Store.SelectDirectory(root);
                using StorageLease lease = StorageLease.Acquire(Store.DirectoryPath);
                TransactionRecovery.RecoverPending(Store.Recovery, registry, reportWarning: ReportWarning);
                ManagerState? existing = LoadStorage(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (existing != null)
                {
                    State = existing;
                }
                else
                {
                    CheckNewStorage();
                    using FileTransaction transaction = BeginTransaction("Initialize central storage");
                    var updatedState = new ManagerState { Paths = CentralPaths.At(root) };
                    foreach (string path in updatedState.Paths.Directories())
                    {
                        LocalFiles.CheckParents(Path.Combine(path, "check"));
                        Directory.CreateDirectory(path);
                    }

                    Commit(updatedState, transaction, cancellationToken);
                }
            }
            catch
            {
                Store.SelectDirectory(previousDirectory);
                throw;
            }

            try
            {
                Store.RememberSelection();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                ReportWarning("Central storage is ready, but its location could not be saved beside the app. " +
                    "Select the same central folder on the next launch. " + error.Message);
            }

            return Task.CompletedTask;
        }, cancellationToken, acquireLease: false);
    }

    private void CheckNewStorage()
    {
        string root = Store.DirectoryPath;
        if (!Directory.Exists(root))
        {
            return;
        }

        CentralPaths paths = CentralPaths.At(root);
        var allowed = paths.Directories().Append(paths.ShaderRoot).Append(Store.Recovery).Append(Store.Staging)
            .Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            LocalFiles.CheckParents(Path.Combine(directory, "check"));
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (LocalFiles.Same(entry, Path.Combine(root, StorageLease.FileName)))
                {
                    continue;
                }
                if (!Directory.Exists(entry) || !allowed.Contains(Path.GetFullPath(entry)))
                {
                    throw new IOException("The selected folder contains files without central storage settings. " +
                        "They were preserved. Choose an empty folder or restore ReShadeCentralized.json: " + root);
                }
                pending.Push(entry);
            }
        }
    }

    public void Dispose() => operationGate.Dispose();
}
