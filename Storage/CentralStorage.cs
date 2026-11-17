using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ReShadeCentralized;

public sealed class CentralStorage(StateStore store, ILayerRegistry registry) : IDisposable {
    private readonly SemaphoreSlim operationGate = new(1, 1);

    public StateStore Store { get; } = store;
    public ManagerState? State { get; private set; }
    public event Action<string>? Warning;

    public ManagerState RequireState() => State ??
        throw new InvalidOperationException("Choose central storage in Setup first.");

    internal void ReportWarning(string message) {
        try {
            Warning?.Invoke(message);
        } catch (Exception error) {
            System.Diagnostics.Trace.TraceWarning("Could not display a storage warning: {0}. {1}", message, error.Message);
        }
    }

    internal FileTransaction BeginTransaction(string description) {
        TransactionRecovery.RecoverPending(Store.Recovery, registry, reportWarning: ReportWarning);
        return new(Store.Recovery, description, registry, ReportWarning);
    }

    internal void Commit(ManagerState state, FileTransaction transaction, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        string savedHash = Store.Save(state, transaction);
        transaction.Commit();
        Store.AcceptSavedState(savedHash);
        State = state;
    }

    public void Open() => Open(CancellationToken.None);

    internal void Open(CancellationToken cancellationToken) {
        if (!operationGate.Wait(0))
            throw new IOException("Central storage is already in use by this app instance.");

        try {
            State = null;
            cancellationToken.ThrowIfCancellationRequested();
            Store.ReadSelection();
            if (!Store.IsSelected)
                return;

            using StorageLease lease = StorageLease.Acquire(Store.DirectoryPath);
            TransactionRecovery.RecoverPending(Store.Recovery, registry, reportWarning: ReportWarning);
            cancellationToken.ThrowIfCancellationRequested();
            State = LoadStorage(cancellationToken);
            if (State == null && Store.PointerPath != null) {
                throw new InvalidDataException("Saved central storage has no registration state: " +
                    Store.DirectoryPath + ". Select the existing central folder in Setup.");
            }
        } catch {
            if (Store.PointerPath != null)
                Store.SelectDirectory(null);

            throw;
        } finally {
            operationGate.Release();
        }
    }

    private ManagerState? LoadStorage(CancellationToken cancellationToken) {
        ManagerState? state = Store.Load(out IReadOnlyList<string> repairs);
        if (state == null || repairs.Count == 0)
            return state;

        using FileTransaction transaction = BeginTransaction("Migrate / repair central storage settings");
        cancellationToken.ThrowIfCancellationRequested();
        string backup = LocalFiles.Contained(Store.DirectoryPath,
            Path.Combine("Backups", "settings-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" +
                Guid.NewGuid().ToString("N") + ".json"));
        if (!transaction.CopyIfAbsent(Store.StatePath, backup))
            throw new IOException("Cannot preserve the original settings before migration: " + backup);
        string savedHash = Store.Save(state, transaction);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        Store.AcceptSavedState(savedHash);
        ReportWarning("Original storage settings were backed up before migration: " + backup);
        foreach (string repair in repairs) {
            ReportWarning(repair);
        }

        return state;
    }

    internal async Task RunAsync(Func<Task> operation, CancellationToken cancellationToken, bool acquireLease = true) {
        await operationGate.WaitAsync(cancellationToken);
        try {
            await Task.Run(async () => {
                cancellationToken.ThrowIfCancellationRequested();
                using StorageLease? lease = acquireLease ? StorageLease.Acquire(Store.DirectoryPath) : null;
                await operation();
            }, cancellationToken);
        } finally {
            operationGate.Release();
        }
    }

    internal async Task ChangeAsync(
        string description,
        Func<ManagerState, FileTransaction, Task> action,
        CancellationToken cancellationToken) {
        await RunAsync(
            async () => {
                ManagerState updatedState = RequireState().Clone();
                using FileTransaction transaction = BeginTransaction(description);
                await action(updatedState, transaction);
                Commit(updatedState, transaction, cancellationToken);
            },
            cancellationToken);
    }

    public Task InitializeAsync(string root, CancellationToken cancellationToken) =>
        RunAsync(() => {
            if (State != null) {
                throw new InvalidOperationException("A central folder is already configured. " +
                    "Changing storage requires a fresh beta setup.");
            }

            string? previousDirectory = Store.IsSelected ? Store.DirectoryPath : null;
            try {
                Store.SelectDirectory(root);
                using StorageLease lease = StorageLease.Acquire(Store.DirectoryPath);
                TransactionRecovery.RecoverPending(Store.Recovery, registry, reportWarning: ReportWarning);
                ManagerState? existing = LoadStorage(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (existing != null)
                    State = existing;
                else {
                    CheckNewStorage();
                    using FileTransaction transaction = BeginTransaction("Initialize central storage");
                    var updatedState = new ManagerState { Paths = CentralPaths.At(root) };
                    foreach (string path in updatedState.Paths.Directories()) {
                        LocalFiles.CheckParents(Path.Combine(path, "check"));
                        Directory.CreateDirectory(path);
                    }

                    Commit(updatedState, transaction, cancellationToken);
                }
            } catch {
                Store.SelectDirectory(previousDirectory);
                throw;
            }

            try {
                Store.RememberSelection();
            } catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
                ReportWarning("Central storage is ready, but its location could not be saved beside the app. " +
                    "Select the same central folder on the next launch. " + error.Message);
            }

            return Task.CompletedTask;
        }, cancellationToken, acquireLease: false);

    private void CheckNewStorage() {
        string root = Store.DirectoryPath;
        if (!Directory.Exists(root))
            return;

        CentralPaths paths = CentralPaths.At(root);
        var allowed = paths.Directories().Append(paths.ShaderRoot).Append(Store.Recovery).Append(Store.Staging)
            .Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out string? directory)) {
            LocalFiles.CheckParents(Path.Combine(directory, "check"));
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory)) {
                if (LocalFiles.Same(entry, Path.Combine(root, StorageLease.FileName)))
                    continue;
                if (!Directory.Exists(entry) || !allowed.Contains(Path.GetFullPath(entry))) {
                    throw new IOException("The selected folder contains files without central storage settings. " +
                        "They were preserved. Choose an empty folder or restore ReShadeCentralized.json: " + root);
                }
                pending.Push(entry);
            }
        }
    }

    public void Dispose() => operationGate.Dispose();
}

internal sealed class StorageLease(SafeFileHandle handle) : IDisposable {
    internal const string FileName = ".ReShadeCentralized.lock";

    internal static StorageLease Acquire(string root) {
        root = Path.GetFullPath(root);
        string path = Path.Combine(root, FileName);
        LocalFiles.CheckParents(Path.Combine(root, "check"));
        Directory.CreateDirectory(root);
        LocalFiles.CheckParents(path);

        SafeFileHandle handle = OpenLockFile(path, 0x80000000, 0, 0, 4, 0x00200000, 0);
        if (handle.IsInvalid) {
            int code = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (code is 32 or 33) {
                throw new IOException("Central storage is in use by another app instance: " + root,
                    new Win32Exception(code));
            }

            throw new IOException("Cannot lock central storage: " + root, new Win32Exception(code));
        }

        try {
            LocalFiles.CheckParents(path);
            if ((File.GetAttributes(handle) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
                || RandomAccess.GetLength(handle) != 0)
                throw new IOException("The central storage lock must be an empty regular file; it was preserved: " + path);

            LocalFiles.EnsureSingleLink(handle);

            return new StorageLease(handle);
        } catch {
            handle.Dispose();
            throw;
        }
    }

    public void Dispose() => handle.Dispose();

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle OpenLockFile(
        string path,
        uint access,
        uint share,
        nint security,
        uint disposition,
        uint flags,
        nint template);
}

internal sealed class StagingDirectory : IDisposable {
    private readonly Action<string> reportWarning;
    private readonly string marker;
    public string Path { get; }

    public StagingDirectory(StateStore store, Action<string> reportWarning) {
        this.reportWarning = reportWarning;
        Path = System.IO.Path.Combine(store.Staging, Guid.NewGuid().ToString("N"));
        marker = System.IO.Path.Combine(Path, ".reshade-centralized-" + Guid.NewGuid().ToString("N"));

        try {
            LocalFiles.CheckParents(System.IO.Path.Combine(Path, "check"));
            if (Directory.Exists(Path) || LocalFiles.Exists(Path))
                throw new IOException("The temporary folder already exists.");
            Directory.CreateDirectory(Path);
            LocalFiles.CheckParents(System.IO.Path.Combine(Path, "check"));
            using FileStream ownership = new(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        } catch (IOException error) {
            throw new IOException($"Cannot prepare the download folder {store.Staging}. " +
                "Make sure this path is a writable folder rather than a file, then retry the download.", error);
        }
    }

    public void Dispose() {
        try {
            LocalFiles.CheckParents(System.IO.Path.Combine(Path, "check"));
            if (!Directory.Exists(Path))
                return;
            if (LocalFiles.Link(marker) != null || !File.Exists(marker))
                throw new IOException("The temporary folder ownership marker is missing or changed.");
            Directory.Delete(Path, recursive: true);
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
            reportWarning("Could not safely remove temporary files: " + Path + ". " + error.Message);
        }
    }
}
