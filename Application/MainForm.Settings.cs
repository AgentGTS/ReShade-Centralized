using System.Diagnostics;

namespace ReShadeCentralized;

public sealed partial class MainForm {
    private readonly Label storageLabel = new() {
        AutoSize = true,
        MaximumSize = new Size(800, 0)
    };

    private readonly Label versionsLabel = new() {
        AutoSize = true
    };

    private readonly Label runtimeStatusLabel = new() {
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        AccessibleName = "Installed ReShade",
        Margin = new Padding(3, 0, 16, 0)
    };

    private readonly DarkComboBox vulkanBuildSelector = new() {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Anchor = AnchorStyles.Left,
        Width = 260
    };

    private readonly VulkanGamesList vulkanGamesList = new() {
        Width = 800,
        Height = 105
    };

    private readonly Label vulkanStatusLabel = new() {
        AutoSize = true,
        Dock = DockStyle.Fill,
        AccessibleName = "Vulkan registration status",
        Margin = Padding.Empty
    };

    private readonly Panel vulkanWarningIcon = new() {
        Size = new Size(16, 16),
        Margin = new Padding(0, 2, 8, 0),
        AccessibleName = "Warning"
    };

    private bool refreshingVulkanStatus;
    private string? lastVulkanInspectionError;

    private void BuildSettings() {
        TableLayoutPanel layout = CreatePage("Runtime & storage");
        var viewport = new Panel {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Margin = Padding.Empty
        };

        var sections = new TableLayoutPanel {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(0, 0, 8, 0)
        };
        sections.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        viewport.Controls.Add(sections);
        layout.Controls.Add(viewport, 0, 0);
        layout.SetRowSpan(viewport, 3);

        TableLayoutPanel runtime = CreateSection(sections, "ReShade runtime");
        AddSectionRow(runtime, versionsLabel);
        FlowLayoutPanel runtimeCommands = CreateCommandBar();
        runtimeCommands.Controls.Add(CreateActionButton("Update ReShade", OnUpdateReShadeAsync, primary: true));
        runtimeCommands.Controls.Add(CreateActionButton("Import downloaded installers...", ImportRuntimeAsync));
        AddSectionRow(runtime, runtimeCommands);

        TableLayoutPanel vulkan = CreateSection(sections, "Vulkan \u2014 all games");
        var vulkanStatus = new TableLayoutPanel {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2
        };
        vulkanStatus.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        vulkanStatus.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        vulkanWarningIcon.Paint += (_, e) => e.Graphics.DrawIcon(SystemIcons.Warning,
            vulkanWarningIcon.ClientRectangle);
        vulkanStatus.Controls.Add(vulkanWarningIcon, 0, 0);
        vulkanStatus.Controls.Add(vulkanStatusLabel, 1, 0);
        vulkanStatus.SizeChanged += (_, _) => vulkanStatusLabel.MaximumSize =
            new Size(Math.Max(1, vulkanStatus.ClientSize.Width - LogicalToDeviceUnits(24)), 0);
        AddSectionRow(vulkan, vulkanStatus);

        vulkanBuildSelector.Items.AddRange(Enum.GetValues<RuntimeBuild>().Cast<object>().ToArray());
        vulkanBuildSelector.SelectedItem = RuntimeBuild.Standard;
        vulkanBuildSelector.AccessibleName = "Global Vulkan build";

        FlowLayoutPanel vulkanCommands = CreateCommandBar();
        vulkanBuildSelector.Margin = new Padding(0, 0, 8, 8);
        vulkanCommands.Controls.Add(vulkanBuildSelector);
        vulkanCommands.Controls.Add(CreateActionButton("Apply / repair", OnApplyRepairVulkanAsync));
        vulkanCommands.Controls.Add(CreateActionButton(
            "Remove app-owned layers...", OnRemoveVulkanLayersAsync,
            isAvailable: () => storage.State is { OwnedVulkanRegistrations.Count: > 0 } state
                && !state.Games.Any(game => game.Api == RenderApi.Vulkan)));
        AddSectionRow(vulkan, vulkanCommands);

        TableLayoutPanel externalGames = CreateSection(sections, "Vulkan games");
        vulkanGamesList.AccessibleName = "Existing Vulkan consumers";
        AddSectionRow(externalGames, vulkanGamesList);

        FlowLayoutPanel consumerCommands = CreateCommandBar();
        consumerCommands.Controls.Add(CreateActionButton("Manage game...", OnAdoptSelectedGameAsync,
            isAvailable: () => vulkanGamesList.SelectedItem is VulkanGameEntry { IsManaged: false }));
        vulkanGamesList.SelectedIndexChanged += (_, _) => UpdateActionAvailability();
        AddSectionRow(externalGames, consumerCommands);

        TableLayoutPanel storageSection = CreateSection(sections, "Central storage");
        AddSectionRow(storageSection, storageLabel);
        FlowLayoutPanel storageCommands = CreateCommandBar();
        storageCommands.Controls.Add(CreateActionButton("Open central folder",
            () => OpenFolder(storage.RequireState().Paths.Root)));
        AddSectionRow(storageSection, storageCommands);
    }

    private async Task ImportRuntimeAsync() {
        string? standard = DialogLayout.PickFile(
            this,
            "ReShade installer|*.exe",
            "Select the standard official ReShade installer");
        if (standard == null)
            return;

        string? addon = DialogLayout.PickFile(
            this,
            "ReShade installer|*.exe",
            $"Select the official {DisplayNames.For(RuntimeBuild.FullAddon)} ReShade installer");
        if (addon != null) {
            await RunOperationAsync(
                "Importing ReShade installers",
                cancellationToken => runtimes.ImportRuntimeAsync(standard, addon, cancellationToken));
        }
    }

    private bool CanRefreshVulkanStatus() =>
        tabs.SelectedIndex == 2 && operationCancellation == null && !storageControlsLocked && !storageLoadFailed
        && !closeAfterOperation && !IsDisposed && !Disposing;

    private async Task RefreshVulkanStatusAsync() {
        ViewPresentation? current = presentation;
        if (refreshingVulkanStatus || !CanRefreshVulkanStatus() || current?.State == null)
            return;

        refreshingVulkanStatus = true;
        try {
            VulkanInspection inspection = await Task.Run(() => vulkan.Inspect(current.State));
            if (ReferenceEquals(presentation, current) && CanRefreshVulkanStatus())
                RefreshVulkanStatus(inspection);
        } catch (Exception error) {
            if (CanRefreshVulkanStatus())
                WriteLog("Could not refresh Vulkan status: " + error.Message);
        } finally {
            refreshingVulkanStatus = false;
        }
    }

    private void RefreshVulkanStatus(VulkanInspection inspection) {
        bool pendingBuild = storage.State != null && vulkanBuildSelector.SelectedItem is RuntimeBuild selectedBuild
            && selectedBuild != storage.State.VulkanBuild;
        bool applied = inspection.IsApplied && !pendingBuild;

        vulkanStatusLabel.Text = pendingBuild ? "Build choice applies to app-owned layers. Choose Apply / repair." :
            inspection.Message;
        vulkanStatusLabel.ForeColor = applied || SystemInformation.HighContrast
            ? DarkTheme.Muted : Color.FromArgb(242, 199, 91);
        vulkanWarningIcon.Visible = !applied;
        vulkanGamesList.SetEntries(inspection.Consumers.Select(executable => new VulkanGameEntry(
            executable,
            storage.State?.Games.Any(game => game.Api == RenderApi.Vulkan && game.Executable != null
                && LocalFiles.Same(game.Executable, executable)) == true)));

        vulkanGamesList.Parent!.Visible = vulkanGamesList.Items.Count > 0;
        if (inspection.Error != null && inspection.Error != lastVulkanInspectionError)
            WriteLog("Vulkan registration check: " + inspection.Error);

        lastVulkanInspectionError = inspection.Error;
        UpdateActionAvailability();
    }

    private static Task OpenFolder(string path) {
        Process.Start(new ProcessStartInfo("explorer.exe") {
            UseShellExecute = true,
            ArgumentList = { path }
        });
        return Task.CompletedTask;
    }

    private Task OnUpdateReShadeAsync() =>
        RunOperationAsync(
            "Updating ReShade",
            cancellationToken => runtimes.UpdateRuntimeAsync(progress, cancellationToken));

    private async Task OnRemoveVulkanLayersAsync() {
        if (MessageBox.Show(
            this,
            "Remove this app's shared Vulkan layer registrations?\n\n" +
            "Games outside the app that share this layer may lose ReShade. " +
            "Removal is blocked if games are still recorded as using it. " +
            "Other installers' registrations and all runtime files will be kept.",
            "Remove app-owned Vulkan layers",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        await RunOperationAsync("Removing app-owned Vulkan layers", async cancellationToken => {
            VulkanRemovalResult result = await vulkan.RemoveVulkanSupportAsync(storage, cancellationToken);
            WriteLog($"Removed {result.RemovedCount} app-owned Vulkan layer registrations. Runtime files were kept.");

            foreach (string preserved in result.Preserved) {
                WriteLog("Kept: " + preserved);
            }
        });
    }

    private Task OnApplyRepairVulkanAsync() =>
        RunOperationAsync(
            "Applying global Vulkan build",
            cancellationToken => vulkan.SetVulkanBuildAsync(
                storage,
                (RuntimeBuild)vulkanBuildSelector.SelectedItem!,
                cancellationToken));

    private async Task OnAdoptSelectedGameAsync() {
        if (vulkanGamesList.SelectedItem is not VulkanGameEntry { IsManaged: false } entry) {
            throw new InvalidOperationException("Open Runtime & storage > Vulkan games and select " +
                "an unmanaged game before choosing Manage game. Edit managed games from the Games tab.");
        }

        string executablePath = entry.Executable;
        var game = new GameProfile {
            Name = Path.GetFileName(executablePath),
            Executable = executablePath,
            Api = RenderApi.Vulkan
        };
        await ShowGameDialogAsync("Adopting Vulkan game", game);
    }

    private static string GetInstalledRuntimeVersion(ManagerState? state, RuntimeBuild build) {
        if (state == null)
            return RuntimeNotInstalled;

        string directory = state.Paths.Binaries(build);
        string[] binaries = [Path.Combine(directory, "ReShade32.dll"), Path.Combine(directory, "ReShade64.dll")];
        int present = binaries.Count(File.Exists);
        if (present != binaries.Length)
            return present == 0 ? RuntimeNotInstalled : "Incomplete";

        try {
            string[] versions = binaries
                .Select(path =>
                    FileVersionInfo.GetVersionInfo(path).ProductVersion?.Split('+')[0] ?? "Unknown version")
                .Distinct()
                .ToArray();
            return versions.Length == 1 ? versions[0] : "Version mismatch";
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
            return "Unavailable";
        }
    }

    private const string RuntimeNotInstalled = "Not installed";
    private readonly RuntimeUpdateService runtimeUpdateService = new();
    private readonly ToolTip runtimeUpdateToolTip = new();
    private readonly RefreshButton runtimeUpdateButton = new() {
        AccessibleName = "Check for ReShade updates",
        AccessibleDescription = "Check the official GitHub repository for a newer stable ReShade version."
    };

    private readonly FlowLayoutPanel runtimeUpdateNotice = new() {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Anchor = AnchorStyles.Left,
        WrapContents = false,
        Margin = Padding.Empty,
        Visible = false
    };

    private readonly ApiCheckIcon runtimeUpdateIcon = new() {
        State = ApiCheckState.Detected,
        ForeColor = DarkTheme.Accent,
        Margin = new Padding(0, 3, 6, 0),
        AccessibleName = "ReShade update information",
        AccessibleDescription = "An updated ReShade runtime is available."
    };

    private readonly LinkLabel runtimeUpdateLabel = new() {
        AutoSize = true,
        LinkColor = DarkTheme.Accent,
        ActiveLinkColor = DarkTheme.Accent,
        VisitedLinkColor = DarkTheme.Accent,
        DisabledLinkColor = DarkTheme.Muted,
        LinkBehavior = LinkBehavior.HoverUnderline,
        ForeColor = DarkTheme.Muted,
        Margin = Padding.Empty,
        AccessibleName = "ReShade update status"
    };

    private CancellationTokenSource? runtimeUpdateCancellation;
    private Version? latestRuntimeVersion;
    private RuntimeUpdateState runtimeUpdateState;

    private enum RuntimeUpdateState {
        NotChecked,
        Checking,
        Verified,
        Unavailable
    }

    private void BuildRuntimeUpdateNotice(TableLayoutPanel footer) {
        runtimeUpdateNotice.Controls.Add(runtimeUpdateIcon);
        runtimeUpdateNotice.Controls.Add(runtimeUpdateLabel);
        runtimeUpdateNotice.Controls.Add(new Label {
            AutoSize = true,
            Text = "|",
            Margin = new Padding(3, 0, 0, 0)
        });
        footer.Controls.Add(runtimeUpdateNotice, 1, 0);
        runtimeUpdateLabel.LinkClicked += (_, _) => {
            if (runtimeUpdateLabel.Links.Count > 0 && operationCancellation == null
                && !storageControlsLocked && !closeAfterOperation)
                tabs.SelectedIndex = 2;
        };
        runtimeUpdateButton.Click += async (_, _) => await CheckRuntimeUpdatesAsync();
        runtimeUpdateToolTip.SetToolTip(runtimeUpdateButton, "Check for ReShade updates");
        runtimeUpdateLabel.MouseEnter += (_, _) => {
            if (runtimeUpdateLabel.Links.Count > 0)
                runtimeUpdateLabel.BackColor = DarkTheme.Hover;
        };
        runtimeUpdateLabel.MouseLeave += (_, _) => runtimeUpdateLabel.BackColor = Color.Transparent;
        FormClosing += (_, _) => runtimeUpdateCancellation?.Cancel();
    }

    private async Task CheckRuntimeUpdatesAsync() {
        if (runtimeUpdateCancellation != null || closeAfterOperation || IsDisposed || Disposing)
            return;

        if (storage.State == null || storageLoadFailed) {
            runtimeUpdateState = RuntimeUpdateState.Unavailable;
            latestRuntimeVersion = null;
            RefreshRuntimeUpdateNotice();
            return;
        }

        using var cancellation = new CancellationTokenSource();
        runtimeUpdateCancellation = cancellation;
        runtimeUpdateState = RuntimeUpdateState.Checking;
        latestRuntimeVersion = null;
        runtimeUpdateButton.Enabled = false;
        RefreshRuntimeUpdateNotice();

        try {
            Version available = await runtimeUpdateService.CheckAsync(cancellation.Token);
            if (cancellation.IsCancellationRequested || IsDisposed || Disposing || closeAfterOperation)
                return;

            latestRuntimeVersion = available;
            runtimeUpdateState = RuntimeUpdateState.Verified;
            RefreshRuntimeUpdateNotice();
        } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
        } catch (Exception error) {
            Trace.TraceWarning("ReShade update check unavailable: {0}", error.Message);
            if (!cancellation.IsCancellationRequested && !IsDisposed && !Disposing && !closeAfterOperation) {
                runtimeUpdateState = RuntimeUpdateState.Unavailable;
                RefreshRuntimeUpdateNotice();
            }
        } finally {
            if (ReferenceEquals(runtimeUpdateCancellation, cancellation))
                runtimeUpdateCancellation = null;

            if (!IsDisposed && !Disposing && !closeAfterOperation)
                runtimeUpdateButton.Enabled = true;
        }
    }

    private void RefreshRuntimeUpdateNotice() {
        runtimeUpdateLabel.Links.Clear();
        runtimeUpdateLabel.BackColor = Color.Transparent;
        runtimeUpdateLabel.Text = "";
        runtimeUpdateIcon.Visible = false;
        bool showStatus = runtimeUpdateState is RuntimeUpdateState.Verified or RuntimeUpdateState.Unavailable;
        runtimeUpdateNotice.Visible = showStatus;
        if (!showStatus)
            return;

        string description = "The ReShade update status could not be verified. Use the check button to try again.";
        bool canOpenRuntimeSettings = false;
        runtimeUpdateLabel.Text = "ReShade update unavailable";
        if (runtimeUpdateState == RuntimeUpdateState.Verified && latestRuntimeVersion is { } latest) {
            IEnumerable<string>? versions = presentation?.RuntimeVersions.Values;
            var installedVersions = new List<Version>();
            foreach (string text in versions ?? []) {
                if (Version.TryParse(text, out Version? installed))
                    installedVersions.Add(new Version(installed.Major, installed.Minor, Math.Max(0, installed.Build)));
            }

            Version? newestInstalled = installedVersions.Max();
            if (newestInstalled != null && newestInstalled < latest) {
                runtimeUpdateLabel.Text = $"ReShade {latest} update available";
                canOpenRuntimeSettings = true;
                description = $"ReShade {latest} is available. " +
                    "Open Runtime & storage > Update ReShade. Close affected games before updating.";
            } else if (newestInstalled != null) {
                runtimeUpdateLabel.Text = "ReShade is up to date";
                description = $"At least one installed ReShade build is up to date with official stable version {latest}. " +
                    "Standard and add-on builds are compared using the newest known installed version.";
            } else if (versions?.All(text => text == RuntimeNotInstalled) == true) {
                runtimeUpdateLabel.Text = $"Get ReShade {latest}";
                canOpenRuntimeSettings = true;
                description = $"ReShade {latest} is the latest stable release. Open Runtime & storage > " +
                    "Update ReShade to install it.";
            } else {
                runtimeUpdateLabel.Text = $"ReShade {latest} verified";
                description = $"ReShade {latest} is the latest stable release. The installed runtime version could not be determined.";
            }
        }

        runtimeUpdateIcon.Visible = canOpenRuntimeSettings;
        runtimeUpdateLabel.LinkArea = canOpenRuntimeSettings
            ? new LinkArea(0, runtimeUpdateLabel.Text.Length) : new LinkArea(0, 0);
        runtimeUpdateLabel.AccessibleDescription = description;
        runtimeUpdateIcon.AccessibleDescription = description;
        runtimeUpdateToolTip.SetToolTip(runtimeUpdateLabel, description);
        runtimeUpdateToolTip.SetToolTip(runtimeUpdateIcon, description);
    }

    protected override void Dispose(bool disposing) {
        if (disposing) {
            runtimeUpdateCancellation?.Cancel();
            runtimeUpdateService.Dispose();
            runtimeUpdateToolTip.Dispose();
        }

        base.Dispose(disposing);
    }
}
