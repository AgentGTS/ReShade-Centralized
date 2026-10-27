using System.Diagnostics;

namespace ReShadeCentralized;

public sealed partial class MainForm
{
    private void BuildSettings()
    {
        TableLayoutPanel layout = CreatePage("Runtime & storage");
        var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
        var sections = new TableLayoutPanel
        {
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
        var vulkanStatus = new TableLayoutPanel
        {
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
        Button applyVulkan = CreateActionButton("Apply / repair", OnApplyRepairVulkanAsync);
        vulkanCommands.Controls.Add(vulkanBuildSelector);
        vulkanCommands.Controls.Add(applyVulkan);
        vulkanCommands.Controls.Add(CreateActionButton("Remove app-owned layers...", OnRemoveVulkanLayersAsync,
            isAvailable: () => manager.State is { OwnedVulkanRegistrations.Count: > 0 } state &&
                !state.Games.Any(game => game.Api == RenderApi.Vulkan)));
        vulkanCommands.Layout += (_, _) =>
        {
            int heightDifference = applyVulkan.Height - vulkanBuildSelector.Height;
            if (heightDifference != 0)
            {
                vulkanBuildSelector.ItemHeight = Math.Max(1, vulkanBuildSelector.ItemHeight + heightDifference);
            }
        };
        AddSectionRow(vulkan, vulkanCommands);
        TableLayoutPanel externalGames = CreateSection(sections, "Vulkan games");
        vulkanGamesList.Dock = DockStyle.Fill;
        vulkanGamesList.AccessibleName = "Existing Vulkan consumers";
        AddSectionRow(externalGames, vulkanGamesList);
        FlowLayoutPanel consumerCommands = CreateCommandBar();
        consumerCommands.Controls.Add(CreateActionButton("Manage game...", OnAdoptSelectedGameAsync,
            isAvailable: () => vulkanGamesList.SelectedItem is VulkanGameEntry { IsManaged: false }));
        vulkanGamesList.SelectedIndexChanged += (_, _) => UpdateActionAvailability();
        AddSectionRow(externalGames, consumerCommands);

        TableLayoutPanel storage = CreateSection(sections, "Central storage");
        AddSectionRow(storage, storageLabel);
        FlowLayoutPanel storageCommands = CreateCommandBar();
        storageCommands.Controls.Add(CreateActionButton("Open central folder", OnOpenCentralFolderAsync));
        AddSectionRow(storage, storageCommands);

        TableLayoutPanel application = CreateSection(sections, "Application");
        FlowLayoutPanel applicationCommands = CreateCommandBar();
        applicationCommands.Controls.Add(CreateActionButton("Check app release", OnCheckAppReleaseAsync));
        applicationCommands.Controls.Add(CreateActionButton("Project releases", OnProjectReleasesAsync));
        AddSectionRow(application, applicationCommands);
    }

    private async Task ImportRuntimeAsync()
    {
        using var first = new OpenFileDialog
        {
            Title = "Select the standard official ReShade installer",
            Filter = "ReShade installer|*.exe"
        };
        if (first.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        using var second = new OpenFileDialog
        {
            Title = $"Select the official {DisplayNames.For(RuntimeBuild.FullAddon)} ReShade installer",
            Filter = "ReShade installer|*.exe"
        };
        if (second.ShowDialog(this) == DialogResult.OK)
        {
            await RunOperationAsync(
                "Importing ReShade installers",
                cancellationToken => manager.ImportRuntimeAsync(first.FileName, second.FileName, cancellationToken));
        }
    }

    private void RefreshVulkanStatus(VulkanInspection? prepared = null)
    {
        VulkanInspection inspection = prepared ?? manager.InspectVulkan();
        bool pendingBuild = manager.State != null && vulkanBuildSelector.SelectedItem is RuntimeBuild selectedBuild &&
            selectedBuild != manager.State.VulkanBuild;
        bool applied = inspection.IsApplied && !pendingBuild;
        vulkanStatusLabel.Text = pendingBuild ? "Build choice applies to app-owned layers. Choose Apply / repair." :
            inspection.Message;
        vulkanStatusLabel.ForeColor = applied || SystemInformation.HighContrast
            ? DarkTheme.Muted : Color.FromArgb(242, 199, 91);
        vulkanWarningIcon.Visible = !applied;
        vulkanGamesList.SetEntries(inspection.Consumers.Select(executable => new VulkanGameEntry(executable,
            manager.State?.Games.Any(game => game.Api == RenderApi.Vulkan && game.Executable != null &&
                FileTransaction.Same(game.Executable, executable)) == true)));

        vulkanGamesList.Parent!.Visible = vulkanGamesList.Items.Count > 0;
        if (inspection.Error != null && inspection.Error != lastVulkanInspectionError)
        {
            WriteLog("Vulkan registration check: " + inspection.Error);
        }
        lastVulkanInspectionError = inspection.Error;
        UpdateActionAvailability();
    }

    private static void OpenFolder(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe")
        {
            UseShellExecute = true,
            ArgumentList = { path }
        });

    private Task OnUpdateReShadeAsync()
    {
        return RunOperationAsync(
            "Updating ReShade",
            cancellationToken => manager.UpdateRuntimeAsync(progress, cancellationToken));
    }

    private async Task OnRemoveVulkanLayersAsync()
    {
        if (MessageBox.Show(this,
            "Remove this app's shared Vulkan layer registrations?\n\n" +
            "Games outside the app that share this layer may lose ReShade. " +
            "Removal is blocked if games are still recorded as using it. " +
            "Other installers' registrations and all runtime files will be kept.",
            "Remove app-owned Vulkan layers", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        await RunOperationAsync("Removing app-owned Vulkan layers", async cancellationToken =>
        {
            VulkanRemovalResult result = await manager.RemoveVulkanSupportAsync(cancellationToken);
            WriteLog($"Removed {result.Removed.Count} app-owned Vulkan layer registrations. Runtime files were kept.");
            foreach (string preserved in result.Preserved)
            {
                WriteLog("Kept: " + preserved);
            }
        });
    }

    private Task OnApplyRepairVulkanAsync()
    {
        return RunOperationAsync(
            "Applying global Vulkan build",
            cancellationToken => manager.SetVulkanBuildAsync(
                (RuntimeBuild)vulkanBuildSelector.SelectedItem!,
                cancellationToken));
    }

    private async Task OnAdoptSelectedGameAsync()
    {
        if (vulkanGamesList.SelectedItem is not VulkanGameEntry { IsManaged: false } entry)
        {
            throw new InvalidOperationException("Open Runtime & storage > Vulkan games and select " +
                "an unmanaged game before choosing Manage game. Edit managed games from the Games tab.");
        }

        string executablePath = entry.Executable;
        var game = new GameProfile
        {
            Name = Path.GetFileName(executablePath),
            Executable = executablePath,
            Api = RenderApi.Vulkan
        };
        await ShowGameDialogAsync("Adopting Vulkan game", game);
    }

    private Task OnOpenCentralFolderAsync()
    {
        OpenFolder(RequireState().Paths.Root);
        return Task.CompletedTask;
    }

    private Task OnCheckAppReleaseAsync()
    {
        return RunOperationAsync("Checking fork releases", async cancellationToken =>
        {
            string? release = await manager.CheckReleaseAsync(AppVersion.IsPrerelease, cancellationToken);
            if (release == null)
            {
                WriteLog(AppVersion.IsPrerelease ?
                    "This fork has no published release yet." : "This fork has no published stable release yet.");
                return;
            }

            int comparison = ReleaseVersion.Parse(release).CompareTo(AppVersion);
            if (comparison > 0)
            {
                WriteLog($"App update available: {release} (installed: {AppVersion.Text}). Open Project releases.");
            }
            else if (comparison == 0)
            {
                WriteLog($"App is up to date: {AppVersion.Text}.");
            }
            else
            {
                WriteLog($"Installed app {AppVersion.Text} is newer than the latest published release, {release}.");
            }
        });
    }

    private Task OnProjectReleasesAsync()
    {
        Process.Start(new ProcessStartInfo("https://github.com/AgentGTS/ReShade-Centralized/releases")
        {
            UseShellExecute = true
        });
        return Task.CompletedTask;
    }
}
