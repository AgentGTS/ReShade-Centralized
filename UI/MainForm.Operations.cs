namespace ReShadeCentralized;

public sealed partial class MainForm
{
    private async Task InitializeAsync()
    {
        bool closeAfterSetup = false;
        await RunOperationAsync("Loading central storage", async cancellationToken =>
        {
            await Task.Run(manager.Open, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            while (manager.State == null)
            {
                using var setup = setupDialogFactory?.Invoke() ?? new SetupDialog();
                if (setup.ShowDialog(this) != DialogResult.OK)
                {
                    closeAfterSetup = true;
                    throw new OperationCanceledException();
                }
                await manager.InitializeAsync(setup.CentralRoot, cancellationToken);
            }
            storageLoading.Report(new("Loading shader and compatibility catalogs"));
            catalog = await Task.Run(async () =>
            {
                if (!manager.Shaders.HasCachedCatalog ||
                    !File.Exists(Path.Combine(manager.Store.DirectoryPath, CompatibilityCatalog.CacheName)))
                {
                    return await manager.Shaders.CatalogAsync(progress, cancellationToken);
                }
                return manager.Shaders.CachedCatalog(progress);
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
        bool completed = false;
        bool canceled = false;
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
            completed = true;
            if (reportCompletion)
            {
                WriteLog("Finished: " + description);
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            canceled = true;
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
                storageLoadFailed = showStorageLoading && !completed;
                if (showStorageLoading)
                {
                    storageLoading.EndLoading(completed ? null : canceled ?
                        "Loading cancelled" : "Central storage unavailable");
                }
                cancelButton.Text = storageLoadFailed ? "Retry" : "Cancel";
                cancelButton.Enabled = storageLoadFailed;
                tabs.Enabled = manager.State != null && !storageLoadFailed;
                statusLabel.Text = completed ? "Ready" : canceled ? "Cancelled" : "Operation failed";
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
        if (errorReporter != null)
        {
            errorReporter(error);
            return;
        }
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
