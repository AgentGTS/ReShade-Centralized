namespace ReShadeCentralized;

public sealed partial class MainForm
{
    private async Task InitializeAsync()
    {
        bool closeAfterSetup = false;
        await RunOperationAsync("Loading central storage", async cancellationToken =>
        {
            await Task.Run(storage.Open, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            while (storage.State == null)
            {
                using var setup = new SetupDialog();
                if (setup.ShowDialog(this) != DialogResult.OK)
                {
                    closeAfterSetup = true;
                    throw new OperationCanceledException();
                }
                await storage.InitializeAsync(setup.CentralRoot, cancellationToken);
            }
            storageLoading.Report(new("Loading shader and compatibility catalogs"));
            catalog = await Task.Run(async () =>
            {
                CompatibilityCatalog.LoadCache(Path.Combine(storage.Store.DirectoryPath, CompatibilityCatalog.CacheName));
                if (!shaderCatalog.HasCachedCatalog ||
                    !File.Exists(Path.Combine(storage.Store.DirectoryPath, CompatibilityCatalog.CacheName)))
                {
                    return await RefreshCatalogsAsync(cancellationToken);
                }
                return shaderCatalog.CachedCatalog(progress);
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            SynchronizePackageChoices();
        }, showStorageLoading: true);
        if (closeAfterSetup && !IsDisposed)
        {
            Close();
        }
    }

    private async Task<bool> RunOperationAsync(
        string description,
        Func<CancellationToken, Task> action,
        bool reportCompletion = true,
        bool showStorageLoading = false)
    {
        if (operationCancellation != null || IsDisposed || Disposing)
        {
            return false;
        }

        operationCancellation = new();
        CancellationTokenSource currentOperation = operationCancellation;
        OperationResult result = OperationResult.Failed;
        bool presentationApplied = false;
        storageLoadFailed = false;
        if (showStorageLoading)
        {
            SetStorageControlsLocked(true);
        }
        tabs.Enabled = false;
        cancelButton.Text = "Cancel";
        cancelButton.Enabled = true;
        statusLabel.Text = description;
        WriteLog(description);
        IProgress<StorageLoadProgress>? loadingProgress = null;
        if (showStorageLoading)
        {
            storageLoading.BeginLoading();
            storageLoading.BringToFront();
            storageLoading.Update();
            loadingProgress = new Progress<StorageLoadProgress>(value =>
            {
                if (!IsDisposed && !closeAfterOperation && ReferenceEquals(operationCancellation, currentOperation))
                {
                    storageLoading.Report(value);
                }
            });
        }
        try
        {
            await action(currentOperation.Token);
            currentOperation.Token.ThrowIfCancellationRequested();
            if (IsDisposed || Disposing)
            {
                throw new OperationCanceledException();
            }
            await RefreshViewAsync(currentOperation.Token, loadingProgress);
            presentationApplied = true;
            currentOperation.Token.ThrowIfCancellationRequested();
            result = OperationResult.Completed;
            if (reportCompletion)
            {
                WriteLog("Finished: " + description);
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            result = OperationResult.Cancelled;
            WriteLog("Cancelled. Completed work was kept; unfinished changes were rolled back.");
            return false;
        }
        catch (Exception error)
        {
            if (showStorageLoading && !IsDisposed && !Disposing)
            {
                storageLoading.EndLoading("Central storage unavailable");
            }
            ReportError(error);
            return false;
        }
        finally
        {
            if (!presentationApplied && !showStorageLoading && !closeAfterOperation && !IsDisposed && !Disposing)
            {
                try
                {
                    await RefreshViewAsync(CancellationToken.None);
                }
                catch (Exception error)
                {
                    ReportError(error);
                }
            }
            currentOperation.Dispose();
            operationCancellation = null;
            if (!IsDisposed && !Disposing)
            {
                storageLoadFailed = showStorageLoading && result != OperationResult.Completed;
                if (showStorageLoading)
                {
                    storageLoading.EndLoading(result == OperationResult.Completed ? null : result == OperationResult.Cancelled ?
                        "Loading cancelled" : "Central storage unavailable");
                }
                cancelButton.Text = storageLoadFailed ? "Retry" : "Cancel";
                cancelButton.Enabled = storageLoadFailed;
                tabs.Enabled = storage.State != null && !storageLoadFailed;
                statusLabel.Text = result switch
                {
                    OperationResult.Completed => "Ready",
                    OperationResult.Cancelled => "Cancelled",
                    _ => "Operation failed"
                };
                if (showStorageLoading || closeAfterOperation)
                {
                    SetStorageControlsLocked(closeAfterOperation);
                }
                if (closeAfterOperation)
                {
                    BeginInvoke(Close);
                }
            }
        }
    }

    private enum OperationResult { Failed, Completed, Cancelled }

    private void WriteLog(string text)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(() => WriteLog(text));
            return;
        }

        if (activityLog.TextLength > 100000)
        {
            activityLog.Text = activityLog.Text[^80000..];
        }

        string message = DateTime.Now.ToString("HH:mm:ss") + "  " + text;
        activityLog.AppendText(message + Environment.NewLine);
#if DEBUG
        Console.WriteLine(message);
#endif
        string summary = text.Replace('\r', ' ').Replace('\n', ' ');
        statusLabel.Text = summary.Length > 100 ? summary[..100] + "\u2026" : summary;
    }

    private void ReportError(Exception error)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }
        WriteLog("Failed: " + error);
        if (closeAfterOperation)
        {
            return;
        }
        MessageBox.Show(
            this,
            UserErrors.Describe(error) + "\n\nTechnical details are in Activity.",
            error is InvalidOperationException or ArgumentException ? "Action unavailable" : "Operation failed",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
