using System.Diagnostics;
using System.Reflection;

namespace ReShadeCentralized;

internal static partial class UiPreview
{
    private static void VerifyApiDetection(string gallery)
    {
        string directory = Path.Combine(gallery, "api-detection", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string executablePath = Path.Combine(directory, "game.exe");
        string alternatePath = Path.Combine(directory, "other.exe");
        CreatePreviewExecutable(executablePath);
        CreatePreviewExecutable(alternatePath);
        var pending = new TaskCompletionSource<RenderApi?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkCount = 0;
        using var dialog = new GameDialog(RuntimeBuild.FullAddon, apiDetector: (_, cancellationToken) =>
        {
            checkCount++;
            return pending.Task.WaitAsync(cancellationToken);
        });
        dialog.Opacity = 0;
        dialog.ShowInTaskbar = false;
        dialog.Show();
        TextBox executable = Descendants(dialog).OfType<TextBox>()
            .Single(control => control.AccessibleName == "Game executable");
        ComboBox api = Descendants(dialog).OfType<ComboBox>()
            .Single(control => control.Items.Contains(RenderApi.Vulkan));
        ComboBox build = Descendants(dialog).OfType<ComboBox>()
            .Single(control => control.Items.Contains(RuntimeBuild.Standard));
        Label hint = Descendants(dialog).OfType<Label>()
            .Single(control => control.AccessibleName == "Graphics API detection");
        ApiCheckIcon statusIcon = Descendants(dialog).OfType<ApiCheckIcon>().Single();
        if (api.SelectedIndex != 0 || build.SelectedIndex != 0 || ((Button)dialog.AcceptButton!).Enabled)
        {
            throw new InvalidOperationException("A fresh installation must start with both placeholders selected.");
        }

        api.SelectedItem = RenderApi.Vulkan;
        if (build.Enabled || !Equals(build.SelectedItem, RuntimeBuild.FullAddon) ||
            build.GetItemText(build.SelectedItem) != "Standard + add-on support (Global)")
        {
            throw new InvalidOperationException("Vulkan must always follow the global build.");
        }
        api.SelectedIndex = 0;
        if (!build.Enabled || build.SelectedIndex != 0)
        {
            throw new InvalidOperationException("Leaving Vulkan must restore an unselected build placeholder.");
        }

        executable.Text = executablePath;
        build.SelectedItem = RuntimeBuild.Standard;
        if (checkCount != 0 || !hint.Visible || hint.Text != "Check graphics API" ||
            ((Button)dialog.AcceptButton!).Enabled)
        {
            throw new InvalidOperationException("Typing a path must neither start detection nor select an API.");
        }

        VerifyExecutableEnterDoesNothing(dialog, executable, () => checkCount);
        VerifyApiCheckAppearance(dialog, ApiCheckState.Ready);
        Capture(dialog, Path.Combine(gallery, "install-manual-api-check.png"));
        var checkLink = (LinkLabel)hint;
        typeof(Control).GetMethod("OnMouseEnter", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(checkLink, [EventArgs.Empty]);
        VerifyApiCheckAppearance(dialog, ApiCheckState.Ready, hovered: true);
        Capture(dialog, Path.Combine(gallery, "install-manual-api-hover.png"));
        typeof(Control).GetMethod("OnMouseLeave", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(checkLink, [EventArgs.Empty]);
        VerifyApiCheckAppearance(dialog, ApiCheckState.Ready);
        ClickApiCheck(dialog);
        if (checkCount != 1 || statusIcon.State != ApiCheckState.Checking || !hint.Text.Contains("Checking"))
        {
            throw new InvalidOperationException("The check link must start detection and display the loading indicator.");
        }
        VerifyApiCheckAppearance(dialog, ApiCheckState.Checking);
        Capture(dialog, Path.Combine(gallery, "install-detecting.png"));
        pending.SetResult(RenderApi.DXGI);
        WaitForDetection(() => statusIcon.State != ApiCheckState.Checking && hint.Text.Contains("Recommended API"));
        if (api.SelectedIndex != 0 || !Equals(build.SelectedItem, RuntimeBuild.Standard))
        {
            throw new InvalidOperationException("A recommendation must leave the user's selections unchanged.");
        }
        VerifyApiCheckAppearance(dialog, ApiCheckState.Detected);
        VerifyButtonStates(dialog, false, "Install");
        Capture(dialog, Path.Combine(gallery, "install-detected.png"));
        api.SelectedItem = RenderApi.DXGI;
        VerifyButtonStates(dialog, true, "Install");
        VerifyExecutableEnterDoesNothing(dialog, executable, () => checkCount);

        build.SelectedIndex = 0;
        VerifyButtonStates(dialog, false, "Install");
        build.SelectedItem = RuntimeBuild.Standard;
        api.SelectedIndex = 0;
        VerifyButtonStates(dialog, false, "Install");
        pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = StartApiDetection(dialog);
        pending.SetResult(null);
        WaitForDetection(() => statusIcon.State != ApiCheckState.Checking && hint.Text.Contains("Could not identify"));
        VerifyApiCheckAppearance(dialog, ApiCheckState.Warning);
        VerifyButtonStates(dialog, false, "Install");
        Capture(dialog, Path.Combine(gallery, "install-api-unknown.png"));

        pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = StartApiDetection(dialog);
        api.SelectedItem = RenderApi.OpenGL;
        pending.SetResult(RenderApi.Vulkan);
        WaitForDetection(() => statusIcon.State != ApiCheckState.Checking && hint.Text.Contains("Recommended API"));
        if (!Equals(api.SelectedItem, RenderApi.OpenGL))
        {
            throw new InvalidOperationException("An in-flight result must preserve a newer manual API selection.");
        }

        api.SelectedIndex = 0;
        pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = StartApiDetection(dialog);
        TaskCompletionSource<RenderApi?> obsolete = pending;
        executable.Text = alternatePath;
        if (hint.Text != "Check graphics API" || statusIcon.State == ApiCheckState.Checking)
        {
            throw new InvalidOperationException("Changing a path must clear the old detection hint and spinner.");
        }
        VerifyApiCheckAppearance(dialog, ApiCheckState.Ready);
        pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ClickApiCheck(dialog);
        obsolete.SetResult(RenderApi.DXGI);
        pending.SetResult(RenderApi.Vulkan);
        WaitForDetection(() => statusIcon.State != ApiCheckState.Checking && hint.Text.Contains("Recommended API"));
        if (api.SelectedIndex != 0 || !Equals(build.SelectedItem, RuntimeBuild.Standard))
        {
            throw new InvalidOperationException("A Vulkan recommendation must not change either selection.");
        }
        VerifyButtonStates(dialog, false, "Install");
        api.SelectedItem = RenderApi.Vulkan;
        if (build.Enabled || !Equals(build.SelectedItem, RuntimeBuild.FullAddon))
        {
            throw new InvalidOperationException("Selecting Vulkan must use the locked global build.");
        }
        VerifyButtonStates(dialog, true, "Install");
        Capture(dialog, Path.Combine(gallery, "install-vulkan-detected.png"));
        api.SelectedItem = RenderApi.DXGI;
        if (!build.Enabled || !Equals(build.SelectedItem, RuntimeBuild.Standard) ||
            build.GetItemText(build.SelectedItem) != "Standard")
        {
            throw new InvalidOperationException("Leaving Vulkan must restore the earlier manual build choice.");
        }

        string warningDirectory = Path.Combine(directory, "unmanaged");
        Directory.CreateDirectory(Path.Combine(warningDirectory, "reshade-shaders"));
        File.WriteAllText(Path.Combine(warningDirectory, "ReShade.ini"), "[GENERAL]\nPerformanceMode=1\n");
        string warningExecutable = Path.Combine(warningDirectory, "game.exe");
        CreatePreviewExecutable(warningExecutable);
        executable.Text = warningExecutable;
        pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ClickApiCheck(dialog);
        Capture(dialog, Path.Combine(gallery, "install-detecting-with-warning.png"));
        pending.SetResult(RenderApi.DXGI);
        WaitForDetection(() => statusIcon.State != ApiCheckState.Checking && hint.Text.Contains("Recommended API"));
        Capture(dialog, Path.Combine(gallery, "install-recommendation-with-warning.png"));

        var existing = new GameProfile
        {
            Executable = executablePath,
            Api = RenderApi.OpenGL,
            Build = RuntimeBuild.Standard
        };
        using var repair = new GameDialog(RuntimeBuild.FullAddon, existing);
        if (repair.Result().Api != RenderApi.OpenGL || repair.Result().Build != RuntimeBuild.Standard)
        {
            throw new InvalidOperationException("Reopening a profile must preserve its saved API and build.");
        }

        pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = StartApiDetection(dialog);
        dialog.Dispose();
        pending.SetResult(RenderApi.DXGI);
        Application.DoEvents();
        VerifyApiDetectionCanFinishAfterClosing(executablePath);
        VerifyApiDetectionIgnoresOutOfOrderResults(executablePath, alternatePath);
        VerifyInvalidExecutableInputs(directory, executablePath);
    }

    private static void VerifyApiDetectionIgnoresOutOfOrderResults(string executablePath, string alternatePath)
    {
        foreach (bool changePath in new[] { false, true })
        {
            foreach (bool oldCheckFails in new[] { false, true })
            {
                var older = new TaskCompletionSource<RenderApi?>(TaskCreationOptions.RunContinuationsAsynchronously);
                var newer = new TaskCompletionSource<RenderApi?>(TaskCreationOptions.RunContinuationsAsynchronously);
                var requests = new Queue<TaskCompletionSource<RenderApi?>>([older, newer]);
                var tokens = new List<CancellationToken>();
                using var dialog = new GameDialog(RuntimeBuild.Standard, apiDetector: (_, cancellationToken) =>
                {
                    tokens.Add(cancellationToken);
                    return requests.Dequeue().Task;
                });
                dialog.Opacity = 0;
                dialog.ShowInTaskbar = false;
                dialog.Show();
                TextBox executable = Descendants(dialog).OfType<TextBox>()
                    .Single(control => control.AccessibleName == "Game executable");
                executable.Text = executablePath;
                ComboBox api = Descendants(dialog).OfType<ComboBox>()
                    .Single(control => control.Items.Contains(RenderApi.OpenGL));
                ComboBox build = Descendants(dialog).OfType<ComboBox>()
                    .Single(control => control.Items.Contains(RuntimeBuild.Standard));
                api.SelectedItem = RenderApi.OpenGL;
                build.SelectedItem = RuntimeBuild.Standard;
                Label hint = Descendants(dialog).OfType<Label>()
                    .Single(control => control.AccessibleName == "Graphics API detection");
                Task olderDetection = StartApiDetection(dialog);
                if (changePath)
                {
                    executable.Text = alternatePath;
                }
                Task newerDetection = StartApiDetection(dialog);
                if (!tokens[0].IsCancellationRequested || tokens[1].IsCancellationRequested)
                {
                    throw new InvalidOperationException("A replacement check must cancel only the earlier request.");
                }

                newer.SetResult(RenderApi.DXGI);
                WaitForDetection(() => newerDetection.IsCompleted);
                string latestHint = hint.Text;
                if (!newerDetection.IsCompletedSuccessfully || !latestHint.Contains("Recommended API: DXGI"))
                {
                    throw new InvalidOperationException("The newest check must display its recommendation.");
                }
                if (oldCheckFails)
                {
                    older.SetException(new IOException("Injected obsolete recommendation failure."));
                }
                else
                {
                    older.SetResult(RenderApi.Vulkan);
                }
                WaitForDetection(() => olderDetection.IsCompleted);
                if (!olderDetection.IsCompletedSuccessfully || hint.Text != latestHint ||
                    dialog.Result().Api != RenderApi.OpenGL || dialog.Result().Build != RuntimeBuild.Standard)
                {
                    throw new InvalidOperationException("Obsolete results must not replace the latest hint or choices.");
                }
                VerifyButtonStates(dialog, true, "Install");
            }
        }
    }

    private static void VerifyApiDetectionCanFinishAfterClosing(string executablePath)
    {
        Action<TaskCompletionSource<RenderApi?>>[] completions =
        [
            pending => pending.SetResult(RenderApi.Vulkan),
            pending => pending.SetException(new InvalidOperationException("Injected late detection failure.")),
            pending => pending.SetCanceled()
        ];
        foreach (DialogResult result in new[] { DialogResult.OK, DialogResult.Cancel })
        {
            foreach (Action<TaskCompletionSource<RenderApi?>> complete in completions)
            {
                var pending = new TaskCompletionSource<RenderApi?>(TaskCreationOptions.RunContinuationsAsynchronously);
                CancellationToken observedCancellation = default;
                using var dialog = new GameDialog(RuntimeBuild.Standard, apiDetector: (_, cancellationToken) =>
                {
                    observedCancellation = cancellationToken;
                    // Simulate a detector that cannot stop immediately, such as an outstanding file read.
                    return pending.Task;
                });
                dialog.Opacity = 0;
                dialog.ShowInTaskbar = false;
                TextBox executable = Descendants(dialog).OfType<TextBox>()
                    .Single(control => control.AccessibleName == "Game executable");
                executable.Text = executablePath;
                ComboBox api = Descendants(dialog).OfType<ComboBox>()
                    .Single(control => control.Items.Contains(RenderApi.Vulkan));
                ComboBox build = Descendants(dialog).OfType<ComboBox>()
                    .Single(control => control.Items.Contains(RuntimeBuild.Standard));
                api.SelectedItem = RenderApi.OpenGL;
                build.SelectedItem = RuntimeBuild.Standard;
                Label hint = Descendants(dialog).OfType<Label>()
                    .Single(control => control.AccessibleName == "Graphics API detection");
                Task? detection = null;
                dialog.Shown += (_, _) =>
                {
                    detection = StartApiDetection(dialog);
                    VerifyButtonStates(dialog, true, "Install");
                    Button close = result == DialogResult.OK ? (Button)dialog.AcceptButton! :
                        (Button)dialog.CancelButton!;
                    close.PerformClick();
                };
                if (dialog.ShowDialog() != result || detection == null || detection.IsCompleted ||
                    !observedCancellation.IsCancellationRequested)
                {
                    throw new InvalidOperationException("Confirm and Cancel must close without waiting for detection.");
                }

                string hintBeforeCompletion = hint.Text;
                var lateUpdates = 0;
                hint.TextChanged += (_, _) => lateUpdates++;
                dialog.SizeChanged += (_, _) => lateUpdates++;
                complete(pending);
                WaitForDetection(() => detection.IsCompleted);
                if (!detection.IsCompletedSuccessfully || hint.Text != hintBeforeCompletion || lateUpdates != 0 ||
                    dialog.Result().Api != RenderApi.OpenGL || dialog.Result().Build != RuntimeBuild.Standard)
                {
                    throw new InvalidOperationException("Late results must not affect a closed dialog or its choices.");
                }
            }
        }

        using var failed = new GameDialog(RuntimeBuild.Standard, apiDetector: (_, _) =>
            Task.FromException<RenderApi?>(new InvalidDataException("Injected advisory failure.")));
        Descendants(failed).OfType<TextBox>()
            .Single(control => control.AccessibleName == "Game executable").Text = executablePath;
        foreach (ComboBox selection in Descendants(failed).OfType<ComboBox>())
        {
            selection.SelectedIndex = 1;
        }
        Task failedDetection = StartApiDetection(failed);
        WaitForDetection(() => failedDetection.IsCompleted);
        Label failedHint = Descendants(failed).OfType<Label>()
            .Single(control => control.AccessibleName == "Graphics API detection");
        if (!failedDetection.IsCompletedSuccessfully || !failedHint.Text.Contains("Could not check"))
        {
            throw new InvalidOperationException("An advisory failure must be handled without blocking installation.");
        }
        VerifyApiCheckAppearance(failed, ApiCheckState.Warning);
        VerifyButtonStates(failed, true, "Install");
    }

    private static void VerifyApiCheckAppearance(GameDialog dialog, ApiCheckState state, bool hovered = false)
    {
        LinkLabel hint = Descendants(dialog).OfType<LinkLabel>().Single();
        ApiCheckIcon icon = Descendants(dialog).OfType<ApiCheckIcon>().Single();
        Color textColor = state switch
        {
            ApiCheckState.Detected => DarkTheme.Accent,
            ApiCheckState.Warning => DarkTheme.Warning,
            _ => DarkTheme.Muted
        };
        Color linkColor = hovered ? DarkTheme.Accent : DarkTheme.Muted;
        Color iconColor = state == ApiCheckState.Ready ? linkColor : textColor;
        if (icon.State != state || hint.ForeColor != textColor || icon.ForeColor != iconColor ||
            hint.LinkColor != linkColor || hint.Links.Count != (state == ApiCheckState.Ready ? 1 : 0))
        {
            throw new InvalidOperationException($"The graphics API check has incorrect styling for {state}.");
        }
    }

    private static Task StartApiDetection(GameDialog dialog) =>
        (Task)typeof(GameDialog).GetMethod("DetectApiAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dialog, null)!;

    private static void ClickApiCheck(GameDialog dialog)
    {
        LinkLabel link = Descendants(dialog).OfType<LinkLabel>()
            .Single(control => control.AccessibleName == "Graphics API detection");
        if (!link.Visible || link.Links.Count != 1)
        {
            throw new InvalidOperationException("A manually entered path must offer a check link above the input.");
        }
        typeof(LinkLabel).GetMethod("OnLinkClicked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(link, [new LinkLabelLinkClickedEventArgs(link.Links[0])]);
    }

    private static void VerifyExecutableEnterDoesNothing(GameDialog dialog, TextBox executable, Func<int> checkCount)
    {
        int before = checkCount();
        executable.Focus();
        bool handled = (bool)typeof(Control).GetMethod("ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(executable, [Keys.Enter])!;
        if (!handled || checkCount() != before || dialog.DialogResult != DialogResult.None || !dialog.Visible)
        {
            throw new InvalidOperationException("Enter in the executable input must neither check nor submit the dialog.");
        }
    }

    private static void CreatePreviewExecutable(string path) => File.Copy(Environment.ProcessPath!, path, true);

    private static void VerifyInvalidExecutableInputs(string directory, string executablePath)
    {
        string malformed = Path.Combine(directory, "not-a-game.exe");
        File.WriteAllText(malformed, "Not a PE executable");
        File.WriteAllText(Path.Combine(directory, "ReShade.ini"), "[GENERAL]\nPerformanceMode=1");
        var requests = 0;
        using var dialog = new GameDialog(RuntimeBuild.Standard, apiDetector: (_, _) =>
        {
            requests++;
            return Task.FromResult<RenderApi?>(RenderApi.DXGI);
        });
        dialog.Opacity = 0;
        dialog.ShowInTaskbar = false;
        dialog.Show();
        TextBox executable = Descendants(dialog).OfType<TextBox>()
            .Single(control => control.AccessibleName == "Game executable");
        foreach (ComboBox selection in Descendants(dialog).OfType<ComboBox>())
        {
            selection.SelectedIndex = 1;
        }
        foreach (string path in new[]
        {
            "game.exe", @"\\server\share\game.exe", @"\\.\pipe\game.exe", executablePath + ":stream.exe", malformed,
            Path.Combine(directory, "missing.exe")
        })
        {
            executable.Text = path;
            VerifyButtonStates(dialog, false, "Install");
            Label warning = Descendants(dialog).OfType<Label>()
                .Single(control => control.AccessibleName == "Installation changes");
            if (warning.Visible)
            {
                throw new InvalidOperationException("Invalid executable paths must not inspect neighboring ReShade files.");
            }
            ClickApiCheck(dialog);
            VerifyApiCheckAppearance(dialog, ApiCheckState.Warning);
            LinkLabel hint = Descendants(dialog).OfType<LinkLabel>().Single();
            if (requests != 0 || !hint.Text.Contains("valid local") ||
                Descendants(dialog).OfType<ApiCheckIcon>().Single().State == ApiCheckState.Checking || executable.Text != path)
            {
                throw new InvalidOperationException("Invalid executable paths must be rejected before starting a check.");
            }
        }
        Capture(dialog, Path.Combine(directory, "install-invalid-executable.png"));
    }

    private static void WaitForDetection(Func<bool> completed)
    {
        var timeout = Stopwatch.StartNew();
        while (!completed())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException("The API detection preview did not complete.");
            }
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }
}
