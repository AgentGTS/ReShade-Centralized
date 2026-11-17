using System.Diagnostics;
#if DEBUG
using System.Threading.Channels;
#endif

namespace ReShadeCentralized;

public sealed partial class MainForm {
#if DEBUG
    private static readonly ChannelWriter<string> debugConsole = StartDebugConsole();

    private static ChannelWriter<string> StartDebugConsole() {
        var messages = Channel.CreateBounded<string>(new BoundedChannelOptions(256) {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _ = Task.Run(async () => {
            try {
                await foreach (string message in messages.Reader.ReadAllAsync()) {
                    Console.WriteLine(message);
                }
            } catch (Exception error) when (error is IOException or ObjectDisposedException) {
                messages.Writer.TryComplete();
            }
        });
        return messages.Writer;
    }
#endif

    private readonly FlatButton cancelButton = new("Cancel") {
        Enabled = false,
        Margin = Padding.Empty
    };

    private readonly FlatButton chooseStorageButton = new("Choose folder...") {
        Visible = false,
        Margin = new Padding(8, 0, 0, 0)
    };
    private CancellationTokenSource? operationCancellation;
    private readonly ShutdownDeadline shutdownDeadline = new();
    private bool closeAfterOperation;

    private sealed record ViewPresentation(
        ManagerState? State,
        List<GameDisplayRow> Games,
        Dictionary<RuntimeBuild, string> RuntimeVersions,
        VulkanInspection Vulkan,
        Dictionary<string, bool> PackageCompleteness);

    private ViewPresentation? presentation;
    private bool storageLoadFailed;

    private readonly StorageLoadingIndicator storageLoading = new();
    private bool storageControlsLocked;

    private void SetStorageControlsLocked(bool locked) {
        storageControlsLocked = locked;
        for (int index = 0; index < 3; index++) {
            tabs.TabPages[index].Enabled = !locked;
        }

        runtimeUpdateButton.Enabled = !locked && runtimeUpdateCancellation == null;
    }

    private Task InitializeAsync() => LoadStorageAsync();

    private async Task ChooseStorageAsync() {
        if (!storageLoadFailed || operationCancellation != null || closeAfterOperation || storage.State != null)
            return;

        using var setup = new SetupDialog();
        if (setup.ShowDialog(this) == DialogResult.OK)
            await LoadStorageAsync(setup.CentralRoot);
    }

    private async Task LoadStorageAsync(string? replacementDirectory = null) {
        bool closeAfterSetup = false;
        await RunOperationAsync("Loading central storage", async cancellationToken => {
            if (replacementDirectory == null)
                await Task.Run(() => storage.Open(cancellationToken), cancellationToken);
            else
                await storage.InitializeAsync(replacementDirectory, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            while (storage.State == null) {
                using var setup = new SetupDialog();
                if (setup.ShowDialog(this) != DialogResult.OK) {
                    closeAfterSetup = true;
                    throw new OperationCanceledException(cancellationToken);
                }

                await storage.InitializeAsync(setup.CentralRoot, cancellationToken);
            }

            storageLoading.Report(new("Loading shader catalog"));
            catalog = await Task.Run(() => shaderCatalog.CatalogAsync(progress, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            SynchronizePackageChoices();
        }, showStorageLoading: true);

        if (closeAfterSetup && !IsDisposed)
            Close();
    }

    private async Task RunOperationAsync(
        string description,
        Func<CancellationToken, Task> action,
        bool reportCompletion = true,
        bool showStorageLoading = false) {
        if (operationCancellation != null || IsDisposed || Disposing)
            return;

        operationCancellation = new();
        CancellationTokenSource currentOperation = operationCancellation;
        OperationResult result = OperationResult.Failed;
        bool presentationApplied = false;
        storageLoadFailed = false;

        if (showStorageLoading)
            SetStorageControlsLocked(true);

        tabs.Enabled = showStorageLoading;
        chooseStorageButton.Visible = false;
        cancelButton.Text = "Cancel";
        cancelButton.Enabled = true;
        WriteLog(description);
        IProgress<StorageLoadProgress>? loadingProgress = null;

        if (showStorageLoading) {
            storageLoading.BeginLoading();
            storageLoading.BringToFront();
            loadingProgress = new Progress<StorageLoadProgress>(value => {
                if (!IsDisposed && !closeAfterOperation && ReferenceEquals(operationCancellation, currentOperation))
                    storageLoading.Report(value);
            });
        }
        try {
            await action(currentOperation.Token);
            currentOperation.Token.ThrowIfCancellationRequested();
            if (IsDisposed || Disposing)
                throw new OperationCanceledException();

            await RefreshViewAsync(currentOperation.Token, loadingProgress);
            presentationApplied = true;
            currentOperation.Token.ThrowIfCancellationRequested();
            result = OperationResult.Completed;
            if (reportCompletion)
                WriteLog("Finished: " + description);
        } catch (OperationCanceledException error) when (currentOperation.IsCancellationRequested
            || error.CancellationToken == currentOperation.Token || IsDisposed || Disposing || closeAfterOperation) {
            result = OperationResult.Cancelled;
            WriteLog("Cancelled. Completed work was kept; unfinished changes were rolled back.");
        } catch (Exception error) {
            if (showStorageLoading && !IsDisposed && !Disposing)
                storageLoading.EndLoading("Central storage unavailable");

            ReportError(error);
        } finally {
            if (!presentationApplied && !showStorageLoading && !closeAfterOperation && !IsDisposed && !Disposing) {
                try {
                    await RefreshViewAsync(CancellationToken.None);
                } catch (Exception error) {
                    ReportError(error);
                }
            }

            currentOperation.Dispose();
            operationCancellation = null;
            if (!IsDisposed && !Disposing) {
                storageLoadFailed = showStorageLoading && result != OperationResult.Completed;
                if (showStorageLoading) {
                    storageLoading.EndLoading(
                        result == OperationResult.Completed ? null
                        : result == OperationResult.Cancelled ? "Loading cancelled"
                        : "Central storage unavailable");
                }

                cancelButton.Text = storageLoadFailed ? "Retry" : "Cancel";
                cancelButton.Enabled = storageLoadFailed && !closeAfterOperation;
                chooseStorageButton.Visible = storageLoadFailed && storage.State == null && !closeAfterOperation;
                tabs.Enabled = !closeAfterOperation;
                statusLabel.Text = result switch {
                    OperationResult.Completed => "Ready",
                    OperationResult.Cancelled => "Cancelled",
                    _ => "Operation failed"
                };

                if (showStorageLoading || closeAfterOperation)
                    SetStorageControlsLocked(storageLoadFailed || closeAfterOperation);

                if (closeAfterOperation)
                    BeginInvoke(Close);
            }
        }
    }

    private enum OperationResult {
        Failed,
        Completed,
        Cancelled
    }

    private void WriteLog(string text) {
        if (IsDisposed)
            return;

        if (InvokeRequired) {
            BeginInvoke(() => WriteLog(text));
            return;
        }

        if (activityLog.TextLength > 100000)
            activityLog.Text = activityLog.Text[^80000..];

        string message = DateTime.Now.ToString("HH:mm:ss") + "  " + text;
        activityLog.AppendText(message + Environment.NewLine);
#if DEBUG
        debugConsole.TryWrite(message);
#endif
        string summary = text.Replace('\r', ' ').Replace('\n', ' ');
        statusLabel.Text = summary.Length > 100 ? summary[..100] + "\u2026" : summary;
    }

    private void ReportError(Exception error) {
        if (IsDisposed || Disposing)
            return;

        WriteLog("Failed: " + error);
        if (closeAfterOperation)
            return;

        MessageBox.Show(
            this,
            UserErrors.Describe(error) + "\n\nTechnical details are in Activity.",
            error is InvalidOperationException or ArgumentException ? "Action unavailable" : "Operation failed",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private ViewPresentation PrepareView(
        CancellationToken cancellationToken,
        IProgress<StorageLoadProgress>? progress,
        IReadOnlyList<ShaderPackage> packages) {
        ManagerState? state = storage.State;
        List<GameDisplayRow> games = PrepareGames(state, cancellationToken, progress, packages);
        var reportWatch = Stopwatch.StartNew();

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new("Checking runtime and Vulkan"));
        Dictionary<RuntimeBuild, string> versions = Enum.GetValues<RuntimeBuild>()
            .ToDictionary(build => build, build => GetInstalledRuntimeVersion(state, build));
        VulkanInspection inspection = vulkan.Inspect(state);
        var completeness = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (InstalledPackage package in state?.Packages ?? []) {
            cancellationToken.ThrowIfCancellationRequested();
            completeness[package.Id] = ShaderFiles.IsPackageComplete(package, state!.Paths);

            if (reportWatch.ElapsedMilliseconds >= 100 || completeness.Count == state.Packages.Count) {
                progress?.Report(new("Checking shader packages", completeness.Count, state.Packages.Count));
                reportWatch.Restart();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new(state, games, versions, inspection, completeness);
    }

    private async Task RefreshViewAsync(
        CancellationToken cancellationToken,
        IProgress<StorageLoadProgress>? progress = null) {
        List<ShaderPackage> currentCatalog = catalog;
        ViewPresentation prepared = await Task.Run(
            () => PrepareView(cancellationToken, progress, currentCatalog),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsDisposed && !Disposing) {
            storageLoading.Report(new("Preparing the grid"));
            ApplyView(prepared);
        }
    }

    private void ApplyView(ViewPresentation prepared) {
        ManagerState? previous = presentation?.State;
        presentation = prepared;
        ManagerState? state = prepared.State;
        Dictionary<RuntimeBuild, string> runtimeVersions = prepared.RuntimeVersions;
        versionsLabel.Text = string.Join("   \u2022   ",
            runtimeVersions.Select(item => $"{DisplayNames.For(item.Key)}: {item.Value}"));
        runtimeStatusLabel.Text = $"Standard: {runtimeVersions[RuntimeBuild.Standard]}   |   " +
            $"Add-on: {runtimeVersions[RuntimeBuild.FullAddon]}";
        RefreshRuntimeUpdateNotice();
        ApplyGames(prepared.Games);
        storageLabel.Text = state?.Paths.Root ?? "Not configured";

        if (state != null && (previous == null || previous.VulkanBuild != state.VulkanBuild
            || !string.Equals(previous.Paths.Root, state.Paths.Root, StringComparison.OrdinalIgnoreCase)))
            vulkanBuildSelector.SelectedItem = state.VulkanBuild;

        RefreshVulkanStatus(prepared.Vulkan);
        FilterPackages();
    }
}
