using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace ReShadeCentralized;

internal static partial class UiPreview
{
    private sealed class LoadingRegistry : ILayerRegistry
    {
        public IEnumerable<LayerRegistration> Enumerate() => [];
        public int? Read(LayerRegistration key) => null;
        public void Write(LayerRegistration key, int? value) =>
            throw new InvalidOperationException("Loading checks must not modify the registry.");
    }

    private static ManagerState CreateLoadingFixture(string root, int games)
    {
        var state = new ManagerState { Paths = CentralPaths.At(root) };
        Directory.CreateDirectory(root);
        foreach (int index in Enumerable.Range(0, games))
        {
            string executable = Path.Combine(root, "games", "Game" + index, "Game" + index + ".exe");
            var game = new GameProfile
            {
                Executable = executable,
                Name = Path.GetFileName(executable),
                Api = RenderApi.DXGI,
                Architecture = CpuArchitecture.X64
            };
            game.Directory = ProfilePaths.DirectoryFor(state, game);
            game.Links.Add(new(Path.Combine(Path.GetDirectoryName(executable)!, "ReShade.ini"),
                ConfigurationService.ConfigPath(game)));
            game.Links.Add(new(Path.Combine(Path.GetDirectoryName(executable)!, "dxgi.dll"),
                Path.Combine(state.Paths.StandardBinaries, "ReShade64.dll")));
            state.Games.Add(game);
        }
        foreach (int index in Enumerable.Range(0, 20))
        {
            var package = new InstalledPackage { Id = "fixture-" + index, Name = "Fixture " + index };
            string directory = ShaderService.PackageDirectory(state.Paths.Shaders, package.Id);
            Directory.CreateDirectory(directory);
            foreach (int file in Enumerable.Range(0, 20))
            {
                string name = "Effect" + file + ".fx";
                File.WriteAllText(Path.Combine(directory, name), "// synthetic loading fixture");
                package.Files["shaders/" + name] = "fixture";
            }
            state.Packages.Add(package);
        }
        File.WriteAllText(Path.Combine(root, "ReShadeCentralized.json"),
            JsonSerializer.Serialize(state, ManagerState.Json));
        File.WriteAllText(Path.Combine(root, ShaderService.CacheName),
            "[standard]\nRequired=1\nRepositoryUrl=https://github.com/test/standard\n" +
            "DownloadUrl=https://example.test/standard.zip\n");
        File.WriteAllText(Path.Combine(root, CompatibilityCatalog.CacheName), "[fixture.exe]\nRenderApi=DXGI\n");
        return state;
    }

    private static void MeasureLoadingBaseline(MainForm main, string output)
    {
        Directory.CreateDirectory(output);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var manager = (Manager)typeof(MainForm).GetField("manager", flags)!.GetValue(main)!;
        PropertyInfo stateProperty = typeof(Manager).GetProperty(nameof(Manager.State))!;
        foreach (int count in new[] { 0, 1, 100, 500, 1000 })
        {
            string root = Path.Combine(output, "store-" + count);
            CreateLoadingFixture(root, count);
            using var loader = new Manager(new StateStore(root), new LoadingRegistry());
            var watch = Stopwatch.StartNew();
            loader.Open();
            double open = watch.Elapsed.TotalMilliseconds;
            stateProperty.SetValue(manager, loader.State);
            foreach (int run in Enumerable.Range(0, 3))
            {
                watch.Restart();
                typeof(MainForm).GetMethod("RefreshView", flags)!.Invoke(main, null);
                double refresh = watch.Elapsed.TotalMilliseconds;
                string line = $"baseline games={count} run={run} open_ms={open:F1} ui_refresh_ms={refresh:F1}";
                Console.WriteLine(line);
                File.AppendAllText(Path.Combine(output, "baseline.txt"), line + Environment.NewLine);
            }
        }
    }

    private static readonly BindingFlags LoadingFlags = BindingFlags.Instance | BindingFlags.NonPublic;

    private static Task InitializeForLoadingCheck(MainForm form) =>
        (Task)typeof(MainForm).GetMethod("InitializeAsync", LoadingFlags)!.Invoke(form, null)!;

    private static Task<bool> RunLoadingCheck(MainForm form, Func<CancellationToken, Task> action) =>
        (Task<bool>)typeof(MainForm).GetMethod("RunOperationAsync", LoadingFlags)!
            .Invoke(form, ["Loading central storage", action, true, true])!;

    private static async Task WaitForLoadingCheckAsync(Func<bool> predicate)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10))
            {
                throw new InvalidOperationException("A loading UI check timed out.");
            }
            await Task.Delay(10);
        }
    }

    private static async Task VerifyStorageLoadingAsync(string gallery)
    {
        string output = Path.Combine(gallery, "loading-checks");
        Directory.CreateDirectory(output);
        foreach (int count in new[] { 0, 1, 100, 500, 1000 })
        {
            string root = Path.Combine(output, "store-" + count);
            CreateLoadingFixture(root, count);
            using var manager = new Manager(new StateStore(root), new LoadingRegistry());
            using var form = new MainForm(manager, initializeOnShow: false, errorReporter: error => throw error)
            {
                Opacity = 0,
                ShowInTaskbar = false
            };
            form.Show();
            StorageLoadingIndicator indicator = Descendants(form).OfType<StorageLoadingIndicator>().Single();
            GamesGrid grid = Descendants(form).OfType<GamesGrid>().Single();
            var elapsed = Stopwatch.StartNew();
            double previousTick = 0;
            double longestGap = 0;
            using var heartbeat = new System.Windows.Forms.Timer { Interval = 15 };
            heartbeat.Tick += (_, _) =>
            {
                double now = elapsed.Elapsed.TotalMilliseconds;
                longestGap = Math.Max(longestGap, now - previousTick);
                previousTick = now;
            };
            foreach (int run in Enumerable.Range(0, 2))
            {
                previousTick = 0;
                longestGap = 0;
                elapsed.Restart();
                heartbeat.Start();
                Task load = InitializeForLoadingCheck(form);
                double shown = elapsed.Elapsed.TotalMilliseconds;
                if (!indicator.Visible || indicator.Text != "Loading central storage")
                {
                    throw new InvalidOperationException("The grid loading state must appear before startup yields.");
                }
                await load;
                double ready = elapsed.Elapsed.TotalMilliseconds;
                await Task.Delay(35);
                heartbeat.Stop();
                if (indicator.Visible || indicator.IsAnimating || grid.Rows.Count != count ||
                    grid.Rows.Cast<DataGridViewRow>().Any(row => row.Cells["InstallationWarning"].Value == null))
                {
                    throw new InvalidOperationException("Ready must include every game and its actual health result.");
                }
                string line = $"optimized games={count} run={run} indicator_ms={shown:F1} " +
                    $"ready_ms={ready:F1} max_tick_gap_ms={longestGap:F1}";
                Console.WriteLine(line);
                File.AppendAllText(Path.Combine(output, "timings.txt"), line + Environment.NewLine);
            }
            if (count == 1000)
            {
                object prepared = typeof(MainForm).GetField("presentation", LoadingFlags)!.GetValue(form)!;
                foreach (DataGridViewAutoSizeColumnsMode mode in new[]
                    { DataGridViewAutoSizeColumnsMode.AllCells, DataGridViewAutoSizeColumnsMode.DisplayedCells })
                {
                    grid.AutoSizeColumnsMode = mode;
                    foreach (int run in Enumerable.Range(0, 2))
                    {
                        grid.DataSource = null;
                        elapsed.Restart();
                        typeof(MainForm).GetMethod("ApplyView", LoadingFlags)!.Invoke(form, [prepared]);
                        string line = $"column_sizing mode={mode} games=1000 run={run} bind_ms=" +
                            elapsed.Elapsed.TotalMilliseconds.ToString("F1");
                        Console.WriteLine(line);
                        File.AppendAllText(Path.Combine(output, "timings.txt"), line + Environment.NewLine);
                    }
                }
            }
        }

        string slowRoot = Path.Combine(output, "slow");
        CreateLoadingFixture(slowRoot, 100);
        using var slowManager = new Manager(new StateStore(slowRoot), new LoadingRegistry());
        slowManager.Open();
        using var slow = new MainForm(slowManager, initializeOnShow: false, errorReporter: _ => { })
        {
            Opacity = 0,
            ShowInTaskbar = false
        };
        slow.Show();
        StorageLoadingIndicator loading = Descendants(slow).OfType<StorageLoadingIndicator>().Single();
        Button cancel = Descendants(slow).OfType<Button>().Single(button => button.Text == "Cancel");
        int animationTicks = 0;
        var animation = (System.Windows.Forms.Timer)typeof(StorageLoadingIndicator)
            .GetField("animation", LoadingFlags)!.GetValue(loading)!;
        animation.Tick += (_, _) => animationTicks++;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> pending = RunLoadingCheck(slow, token => gate.Task.WaitAsync(token));
        VerifyLoadingControlsLocked(slow);
        cancel.PerformClick();
        if (pending.IsCompleted)
        {
            throw new InvalidOperationException("The locked Cancel button must not interrupt storage loading.");
        }
        await Task.Delay(200);
        if (!loading.Visible || !loading.IsBusy || (SystemInformation.UIEffectsEnabled && animationTicks < 2))
        {
            throw new InvalidOperationException(
                "The loading animation must keep receiving UI timer ticks during work.");
        }
        Capture(slow, Path.Combine(gallery, "loading-central-storage.png"));
        loading.Report(new("Checking games", 50, 100));
        if (!loading.AccessibleDescription!.Contains("50") || !loading.AccessibleDescription.Contains("100"))
        {
            throw new InvalidOperationException("Known totals must expose actual completed/total work.");
        }
        Capture(slow, Path.Combine(gallery, "loading-game-counts.png"));
        Size original = slow.Size;
        slow.Size = slow.MinimumSize;
        Capture(slow, Path.Combine(gallery, "loading-minimum-size.png"));
        slow.Size = original;
        loading.Visible = false;
        if (loading.IsAnimating)
        {
            throw new InvalidOperationException("A hidden loading indicator must stop its timer.");
        }
        loading.Visible = true;
        ((CancellationTokenSource)typeof(MainForm).GetField("operationCancellation", LoadingFlags)!
            .GetValue(slow)!).Cancel();
        if (await pending || loading.IsBusy || loading.IsAnimating || cancel.Text != "Retry" || !cancel.Enabled)
        {
            throw new InvalidOperationException(
                "Cancellation must stop animation and expose Retry without claiming Ready.");
        }
        Capture(slow, Path.Combine(gallery, "loading-cancelled.png"));
        cancel.PerformClick();
        await WaitForLoadingCheckAsync(() => !loading.Visible && cancel.Text == "Cancel");
        if (Descendants(slow).OfType<StorageLoadingIndicator>().Count() != 1)
        {
            throw new InvalidOperationException("Retry must reuse the original loading control.");
        }

        File.WriteAllText(slowManager.Store.StatePath, "invalid state");
        await InitializeForLoadingCheck(slow);
        if (loading.IsBusy || loading.IsAnimating || loading.Text != "Central storage unavailable" ||
            cancel.Text != "Retry")
        {
            throw new InvalidOperationException(
                "Failed loads must stop animation and retain an actionable failure state.");
        }
        Capture(slow, Path.Combine(gallery, "loading-failed.png"));
        CreateLoadingFixture(slowRoot, 100);
        await InitializeForLoadingCheck(slow);
        if (loading.Visible)
        {
            throw new InvalidOperationException("A successful retry must clear the failed state.");
        }

        gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> closing = RunLoadingCheck(slow, _ => gate.Task);
        slow.Close();
        if (slow.IsDisposed || !loading.IsBusy)
        {
            throw new InvalidOperationException(
                "Close must wait for an already-running storage step to finish safely.");
        }
        gate.SetResult();
        await closing;
        await WaitForLoadingCheckAsync(() => slow.IsDisposed);

        string missingRoot = Path.Combine(output, "missing-pointer");
        Directory.CreateDirectory(missingRoot);
        File.WriteAllText(Path.Combine(missingRoot, "CentralStorage.txt"),
            Path.Combine(missingRoot, "unavailable-central"));
        var errors = new List<Exception>();
        using var missingManager = new Manager(StateStore.ForApplication(missingRoot), new LoadingRegistry());
        using var missing = new MainForm(missingManager, initializeOnShow: false, errorReporter: errors.Add)
        {
            Opacity = 0,
            ShowInTaskbar = false
        };
        missing.Show();
        await InitializeForLoadingCheck(missing);
        StorageLoadingIndicator missingIndicator = Descendants(missing).OfType<StorageLoadingIndicator>().Single();
        if (errors.Count != 1 || missingIndicator.IsBusy || !missingIndicator.Visible || missingIndicator.IsAnimating)
        {
            throw new InvalidOperationException(
                "Unavailable selected storage must show a stopped, retryable error state.");
        }
        Capture(missing, Path.Combine(gallery, "loading-missing-storage.png"));

        string shownRoot = Path.Combine(output, "shown-startup");
        CreateLoadingFixture(shownRoot, 1);
        using var shownManager = new Manager(new StateStore(shownRoot), new LoadingRegistry());
        using var shownForm = new MainForm(shownManager, initializeOnShow: true, errorReporter: error => throw error)
        {
            Opacity = 0,
            ShowInTaskbar = false
        };
        shownForm.Show();
        StorageLoadingIndicator shownIndicator = Descendants(shownForm).OfType<StorageLoadingIndicator>().Single();
        await WaitForLoadingCheckAsync(() => !shownIndicator.Visible);
        if (Descendants(shownForm).OfType<GamesGrid>().Single().Rows.Count != 1)
        {
            throw new InvalidOperationException("The real Shown event must complete the startup loading pipeline.");
        }

        gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> disposedLoad = RunLoadingCheck(shownForm, _ => gate.Task);
        shownForm.Dispose();
        gate.SetResult();
        await disposedLoad;
        await VerifyFirstUseAsync(gallery);
        Console.WriteLine("PASS Storage loading: 0/1/100/500/1000 games, repeat, responsive animation, counts, " +
            "cancel/retry, failure/recovery, missing storage, hidden timer, safe close, and disposal.");
    }
}
