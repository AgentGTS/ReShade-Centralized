namespace ReShadeCentralized;

public sealed class CentralStorage(StateStore store, ILayerRegistry registry) : IDisposable
{
    private readonly SemaphoreSlim operationGate = new(1, 1);
    public StateStore Store { get; } = store;
    public ManagerState? State { get; private set; }
    public event Action<string>? Warning;

    public ManagerState RequireState() => State ??
        throw new InvalidOperationException("Choose central storage in Setup first.");

    internal void ReportWarning(string message) => Warning?.Invoke(message);

    internal FileTransaction BeginTransaction(string description) =>
        new(Store.Recovery, description, registry, ReportWarning);

    internal void Commit(ManagerState state, FileTransaction transaction, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Store.Save(state, transaction);
        transaction.Commit();
        State = state;
    }

    public void Open()
    {
        Store.ReadSelection();
        if (!Store.IsSelected)
        {
            return;
        }

        TransactionRecovery.RecoverPending(Store.Recovery, registry, reportWarning: ReportWarning);
        State = LoadStorage();
        if (State == null && Store.PointerPath != null)
        {
            throw new InvalidDataException("Saved central storage has no registration state: " +
                Store.DirectoryPath + ". Select the existing central folder in Setup.");
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
        Store.Save(state, transaction);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        foreach (string repair in repairs)
        {
            ReportWarning(repair);
        }
        return state;
    }

    internal async Task RunAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            await Task.Run(operation, cancellationToken);
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
        await RunAsync(async () =>
        {
            ManagerState updatedState = RequireState().Clone();
            using FileTransaction transaction = BeginTransaction(description);
            await action(updatedState, transaction);
            Commit(updatedState, transaction, cancellationToken);
        }, cancellationToken);
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
                TransactionRecovery.RecoverPending(Store.Recovery, registry, reportWarning: ReportWarning);
                ManagerState? existing = LoadStorage(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (existing != null)
                {
                    State = existing;
                }
                else
                {
                    using FileTransaction transaction = BeginTransaction("Initialize central storage");
                    var updatedState = new ManagerState { Paths = CentralPaths.At(root) };
                    foreach (string path in updatedState.Paths.Directories())
                    {
                        LocalFiles.CheckParents(Path.Combine(path, "check"));
                        Directory.CreateDirectory(path);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    Store.Save(updatedState, transaction);
                    transaction.Commit();
                    State = updatedState;
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
        }, cancellationToken);
    }

    public void Dispose() => operationGate.Dispose();
}
