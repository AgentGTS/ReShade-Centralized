using System.Diagnostics;
using System.Reflection;

namespace ReShadeCentralized;

public sealed class MainForm : Form
{
    private static readonly ReleaseVersion AppVersion = ReleaseVersion.Parse(
        typeof(MainForm).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+')[0]);

    private readonly Manager manager;
    private readonly NavigationTabs tabs = new()
    {
        Dock = DockStyle.Fill
    };

    private readonly GamesGrid gamesGrid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        RowHeadersVisible = false
    };

    private readonly PackageList packageList = new()
    {
        Dock = DockStyle.Fill,
        CheckOnClick = true,
        IntegralHeight = false
    };

    private readonly TextBox packageSearchBox = new()
    {
        PlaceholderText = "Search shader packages…",
        Dock = DockStyle.Top
    };

    private readonly TextBox activityLog = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill
    };

    private readonly Label storageLabel = new()
    {
        AutoSize = true,
        MaximumSize = new Size(800, 0)
    };

    private readonly Label versionsLabel = new()
    {
        AutoSize = true,
        Font = DarkTheme.SmallFont
    };

    private readonly Label statusLabel = new()
    {
        AutoSize = true,
        Text = "Ready",
        Margin = new Padding(8)
    };

    private readonly DarkComboBox vulkanBuildSelector = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 150
    };

    private readonly ListBox externalVulkanAppsList = new()
    {
        Width = 800,
        Height = 105,
        HorizontalScrollbar = true
    };

    private readonly FlatButton cancelButton = new("Cancel")
    {
        Enabled = false,
        Margin = Padding.Empty
    };

    private readonly HashSet<string> selectedPackageIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly IProgress<string> progress;
    private List<ShaderPackage> catalog = [];
    private CancellationTokenSource? operationCancellation;
    private bool updatingPackages;

    public MainForm(string applicationDirectory, bool preview = false)
    {
        manager = new(new StateStore(applicationDirectory), new WindowsLayerRegistry());
        Text = $"ReShade Centralized {AppVersion.Text}";
        ClientSize = new(1080, 760);
        MinimumSize = new(900, 650);
        StartPosition = FormStartPosition.CenterScreen;
        Font = DarkTheme.BodyFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        progress = new Progress<string>(WriteLog);
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 4,
            ColumnCount = 1,
            Padding = new Padding(24)
        };
        root.RowStyles.Add(new(SizeType.AutoSize));
        root.RowStyles.Add(new(SizeType.AutoSize));
        root.RowStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.AutoSize));
        Controls.Add(root);
        root.Controls.Add(new Label
        {
            Text = "ReShade Centralized",
            AutoSize = true,
            Font = DarkTheme.TitleFont
        });
        versionsLabel.Margin = new Padding(0, 5, 0, 20);
        root.Controls.Add(versionsLabel);
        root.Controls.Add(tabs);
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 12, 0, 0)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        statusLabel.Anchor = AnchorStyles.Left;
        statusLabel.Margin = Padding.Empty;
        footer.Controls.Add(statusLabel, 0, 0);
        footer.Controls.Add(cancelButton, 1, 0);
        root.Controls.Add(footer);
        cancelButton.Click += (_, _) => operationCancellation?.Cancel();
        BuildGames();
        BuildPackages();
        BuildSettings();
        TableLayoutPanel activity = CreatePage("Activity", "Activity", "Downloads, repairs, and operation details.");
        activityLog.Font = DarkTheme.MonospaceFont;
        activityLog.WordWrap = false;
        activityLog.ScrollBars = ScrollBars.Both;
        activity.Controls.Add(activityLog, 0, 2);
        if (!preview)
        {
            Shown += async (_, _) => await InitializeAsync();
        }

        FormClosing += (_, e) =>
        {
            if (operationCancellation != null)
            {
                e.Cancel = true;
                operationCancellation.Cancel();
                WriteLog("Cancelling the active operation before closing…");
            }
        };
        FormClosed += (_, _) => manager.Dispose();
        DarkTheme.Apply(this);
        RefreshView();
        if (preview)
        {
            LoadBundledCatalog();
        }
    }

    private Button CreateActionButton(string label, Func<Task> action, bool primary = false)
    {
        var button = new FlatButton(label, primary);
        button.Click += async (_, _) =>
        {
            try
            {
                await action();
            }
            catch (Exception e)
            {
                ReportError(e);
            }
        };
        return button;
    }

    private async Task InitializeAsync()
    {
        if (!await RunOperationAsync("Opening manager", _ => Task.Run(manager.Open)))
        {
            Close();
            return;
        }

        if (manager.State == null)
        {
            using var setup = new SetupDialog(manager.Store.DirectoryPath);
            if (setup.ShowDialog(this) != DialogResult.OK)
            {
                Close();
                return;
            }

            await RunOperationAsync(
                "Importing settings",
                cancellationToken => manager.InitializeAsync(setup.CentralRoot, setup.LegacyIni, cancellationToken));
            if (manager.State == null)
            {
                Close();
                return;
            }
        }

        LoadBundledCatalog();
        RefreshView();
    }

    private void LoadBundledCatalog()
    {
        catalog = ShaderService.Parse(ShaderService.Resource("EffectPackages.ini"))
            .Concat(ShaderService.Parse(ShaderService.Resource("ExtraPackages.ini"), true))
            .DistinctBy(package => ShaderService.CanonicalRepository(package.Repository))
            .ToList();
        SynchronizePackageChoices();
        FilterPackages();
    }

    private void BuildGames()
    {
        TableLayoutPanel layout = CreatePage("Games", "Managed games",
            "Shared runtimes. Independent settings, presets, and screenshots for every game.");
        FlowLayoutPanel commands = CreateCommandBar();
        commands.Controls.Add(CreateActionButton("Install to game", OnInstallToGameAsync, primary: true));
        commands.Controls.Add(CreateActionButton("Repair / edit selected", EditSelectedAsync));
        commands.Controls.Add(CreateActionButton("Remove selected", OnRemoveSelectedAsync));
        commands.Controls.Add(CreateActionButton("Repair all", OnRepairAllAsync));
        commands.Controls.Add(CreateActionButton("Open profile", OnOpenProfileAsync));
        layout.Controls.Add(commands, 0, 1);
        layout.Controls.Add(gamesGrid, 0, 2);
        gamesGrid.Margin = Padding.Empty;
        gamesGrid.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex >= 0)
            {
                try
                {
                    await EditSelectedAsync();
                }
                catch (Exception error)
                {
                    ReportError(error);
                }
            }
        };
    }

    private GameProfile GetSelectedGame()
    {
        string? id = gamesGrid.CurrentRow?.Cells["Id"].Value as string;
        return manager.State!.Games.SingleOrDefault(game => game.Id == id) ??
            throw new InvalidOperationException("Select a managed game first.");
    }

    private async Task EditSelectedAsync()
    {
        GameProfile game = GetSelectedGame();
        using var dialog = new GameDialog(manager.State!.VulkanBuild, game);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            await RunOperationAsync(
                "Repairing game",
                cancellationToken => manager.InstallAsync(dialog.Result(), cancellationToken));
        }
    }

    private void BuildPackages()
    {
        TableLayoutPanel layout = CreatePage("Shader packages", "Shader packages",
            "Choose the effects you need. Includes and textures stay organized by package.");
        var search = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Margin = new Padding(0, 0, 0, 16)
        };
        search.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        search.Controls.Add(new Label { Text = "Search packages", AutoSize = true, Margin = new Padding(0, 0, 0, 6) });
        packageSearchBox.Dock = DockStyle.Fill;
        packageSearchBox.Margin = Padding.Empty;
        packageSearchBox.AccessibleName = "Search shader packages";
        search.Controls.Add(packageSearchBox);
        layout.Controls.Add(search, 0, 1);
        packageList.Font = new Font("Segoe UI", 11);
        packageList.Margin = Padding.Empty;
        layout.Controls.Add(packageList, 0, 2);
        var footer = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Margin = new Padding(0, 12, 0, 0)
        };
        footer.Controls.Add(new Label
        {
            Text = "Deselecting a package keeps its installed files. Required packages are always included.",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 12)
        });
        FlowLayoutPanel commands = CreateCommandBar();
        commands.Controls.Add(CreateActionButton(
            "Install / update selected", OnInstallUpdateSelectedAsync, primary: true));
        commands.Controls.Add(CreateActionButton("Update installed packages", OnUpdateInstalledPackagesAsync));
        commands.Controls.Add(CreateActionButton("Refresh catalog", OnRefreshCatalogAsync));
        footer.Controls.Add(commands);
        layout.Controls.Add(footer, 0, 3);
        packageSearchBox.TextChanged += (_, _) => FilterPackages();
        packageList.ItemCheck += (_, e) =>
        {
            if (updatingPackages)
            {
                return;
            }

            var item = (ShaderPackage)packageList.Items[e.Index];
            if (item.Required)
            {
                e.NewValue = CheckState.Checked;
            }

            if (e.NewValue == CheckState.Checked)
            {
                selectedPackageIds.Add(item.Id);
            }
            else
            {
                selectedPackageIds.Remove(item.Id);
            }
        };
    }

    private void SynchronizePackageChoices()
    {
        selectedPackageIds.Clear();
        foreach (string id in manager.State?.SelectedPackages ?? [])
        {
            selectedPackageIds.Add(id);
        }

        if (selectedPackageIds.Count == 0)
        {
            foreach (ShaderPackage? package in catalog.Where(package => package.Required || package.Enabled))
            {
                selectedPackageIds.Add(package.Id);
            }
        }
    }

    private void FilterPackages()
    {
        updatingPackages = true;
        packageList.BeginUpdate();
        packageList.Items.Clear();
        var matchingPackages = catalog.Where(package =>
            $"{package.Name} {package.Description}".Contains(
                packageSearchBox.Text, StringComparison.OrdinalIgnoreCase));
        foreach (ShaderPackage package in matchingPackages)
        {
            packageList.Items.Add(package, selectedPackageIds.Contains(package.Id) || package.Required);
        }

        packageList.EndUpdate();
        updatingPackages = false;
    }

    private Task UpdatePackagesAsync(bool installedOnly)
    {
        return RunOperationAsync("Updating shader packages", async cancellationToken =>
        {
            HashSet<string> ids = installedOnly ?
                manager.State!.Packages.Select(package => package.Id).ToHashSet() :
                selectedPackageIds;
            var chosen = catalog.Where(package => ids.Contains(package.Id)).ToList();
            var unavailablePackages = manager.State!.Packages.Where(package =>
                ids.Contains(package.Id) && !catalog.Any(entry => entry.Id == package.Id));
            foreach (InstalledPackage missing in unavailablePackages)
            {
                chosen.Add(new ShaderPackage
                {
                    Id = missing.Id,
                    Name = missing.Name,
                    Repository = missing.Repository,
                    DownloadUrl = missing.DownloadUrl
                });
            }

            foreach (string message in await manager.UpdatePackagesAsync(
                chosen,
                catalog,
                progress,
                cancellationToken,
                !installedOnly))
            {
                WriteLog(message);
            }

            SynchronizePackageChoices();
            FilterPackages();
        });
    }

    private void BuildSettings()
    {
        TableLayoutPanel layout = CreatePage("Runtime & storage", "Runtime & storage",
            "Update shared binaries, manage the Vulkan layer, and maintain central storage.");
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
        layout.Controls.Add(viewport, 0, 2);

        TableLayoutPanel runtime = CreateSection(sections, "ReShade runtime");
        AddSectionRow(runtime, new Label
        {
            Text = "Standard and full-add-on builds are shared across your managed games.",
            AutoSize = true
        });
        FlowLayoutPanel runtimeCommands = CreateCommandBar();
        runtimeCommands.Controls.Add(CreateActionButton("Update ReShade", OnUpdateReShadeAsync, primary: true));
        runtimeCommands.Controls.Add(CreateActionButton("Import downloaded installers...", ImportRuntimeAsync));
        AddSectionRow(runtime, runtimeCommands);

        TableLayoutPanel vulkan = CreateSection(sections, "Vulkan layer");
        AddSectionRow(vulkan, new Label
        {
            Text = "The build selection applies to every Vulkan consumer. Games keep their own settings and presets.",
            AutoSize = true
        });
        vulkanBuildSelector.Items.AddRange(Enum.GetValues<RuntimeBuild>().Cast<object>().ToArray());
        vulkanBuildSelector.SelectedItem = RuntimeBuild.Standard;
        vulkanBuildSelector.AccessibleName = "Global Vulkan build";
        FlowLayoutPanel vulkanCommands = CreateCommandBar();
        vulkanCommands.Controls.Add(vulkanBuildSelector);
        vulkanCommands.Controls.Add(CreateActionButton("Apply / repair Vulkan", OnApplyRepairVulkanAsync));
        AddSectionRow(vulkan, vulkanCommands);
        AddSectionRow(vulkan, new Label { Text = "Existing consumers", AutoSize = true });
        externalVulkanAppsList.Dock = DockStyle.Fill;
        externalVulkanAppsList.AccessibleName = "Existing Vulkan consumers";
        AddSectionRow(vulkan, externalVulkanAppsList);
        FlowLayoutPanel consumerCommands = CreateCommandBar();
        consumerCommands.Controls.Add(CreateActionButton("Adopt selected game...", OnAdoptSelectedGameAsync));
        consumerCommands.Controls.Add(CreateActionButton("Forget selected record", OnForgetSelectedRecordAsync));
        AddSectionRow(vulkan, consumerCommands);

        TableLayoutPanel storage = CreateSection(sections, "Central storage");
        AddSectionRow(storage, storageLabel);
        FlowLayoutPanel storageCommands = CreateCommandBar();
        storageCommands.Controls.Add(CreateActionButton("Relocate central folder...", OnRelocateCentralFolderAsync));
        storageCommands.Controls.Add(CreateActionButton("Open central folder", OnOpenCentralFolderAsync));
        storageCommands.Controls.Add(CreateActionButton("Restore...", OnRestoreAsync));
        AddSectionRow(storage, storageCommands);

        TableLayoutPanel application = CreateSection(sections, "Application");
        FlowLayoutPanel applicationCommands = CreateCommandBar();
        applicationCommands.Controls.Add(CreateActionButton("Check app release", OnCheckAppReleaseAsync));
        applicationCommands.Controls.Add(CreateActionButton("Project releases", OnProjectReleasesAsync));
        AddSectionRow(application, applicationCommands);
    }

    private TableLayoutPanel CreatePage(string tabName, string title, string description)
    {
        var page = new TabPage(tabName) { Padding = new Padding(20) };
        tabs.TabPages.Add(page);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.Controls.Add(layout);
        var heading = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Margin = new Padding(0, 0, 0, 20)
        };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.Controls.Add(new Label
        {
            Text = title,
            Font = DarkTheme.HeadingFont,
            AutoSize = true,
            Margin = Padding.Empty
        });
        heading.Controls.Add(new Label
        {
            Text = description,
            AutoSize = true,
            Margin = new Padding(0, 7, 0, 0)
        });
        layout.Controls.Add(heading, 0, 0);
        return layout;
    }

    private static FlowLayoutPanel CreateCommandBar() => new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        WrapContents = false,
        Dock = DockStyle.Fill,
        Margin = Padding.Empty
    };

    private static TableLayoutPanel CreateSection(TableLayoutPanel parent, string title)
    {
        var section = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(18),
            Margin = new Padding(0, 0, 0, 16),
            BackColor = DarkTheme.Raised
        };
        section.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        section.Paint += (_, e) =>
        {
            using var border = new Pen(DarkTheme.Border);
            e.Graphics.DrawRectangle(border, 0, 0, section.Width - 1, section.Height - 1);
        };
        parent.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        parent.Controls.Add(section, 0, parent.RowCount++);
        AddSectionRow(section, new Label
        {
            Text = title,
            Font = new Font(DarkTheme.BodyFont, FontStyle.Bold),
            AutoSize = true
        });
        return section;
    }

    private static void AddSectionRow(TableLayoutPanel section, Control control)
    {
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(0, 0, 0, 12);
        section.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        section.Controls.Add(control, 0, section.RowCount++);
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
            Title = "Select the full-add-on official ReShade installer",
            Filter = "ReShade installer|*.exe"
        };
        if (second.ShowDialog(this) == DialogResult.OK)
        {
            await RunOperationAsync(
                "Importing ReShade installers",
                cancellationToken => manager.ImportRuntimeAsync(first.FileName, second.FileName, cancellationToken));
        }
    }

    private async Task<bool> RunOperationAsync(string description, Func<CancellationToken, Task> action)
    {
        if (operationCancellation != null)
        {
            return false;
        }

        operationCancellation = new();
        tabs.Enabled = false;
        cancelButton.Enabled = true;
        statusLabel.Text = description;
        WriteLog(description);
        try
        {
            await action(operationCancellation.Token);
            WriteLog("Finished: " + description);
            return true;
        }
        catch (OperationCanceledException)
        {
            WriteLog("Cancelled. Uncommitted changes were rolled back.");
            return false;
        }
        catch (Exception error)
        {
            ReportError(error);
            return false;
        }
        finally
        {
            operationCancellation?.Dispose();
            operationCancellation = null;
            cancelButton.Enabled = false;
            tabs.Enabled = manager.State != null;
            statusLabel.Text = "Ready";
            RefreshView();
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

        activityLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + text + Environment.NewLine);
        string summary = text.Replace('\r', ' ').Replace('\n', ' ');
        statusLabel.Text = summary.Length > 100 ? summary[..100] + "…" : summary;
    }

    private void ReportError(Exception error)
    {
        WriteLog("Failed: " + error);
        MessageBox.Show(
            this,
            error.Message + "\n\nSee Activity for details. Backups are retained for restore.",
            "Operation failed",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private void RefreshView()
    {
        ManagerState? state = manager.State;
        string runtimeVersions = state == null ?
            "One runtime location. Separate game profiles." :
            string.Join(
            "   •   ",
            Enum.GetValues<RuntimeBuild>()
            .Select(item => item +
                ": " + state.RuntimeVersions.GetValueOrDefault(
                    item,
                    "not downloaded")));
        versionsLabel.Text = $"App: {AppVersion.Text}   •   {runtimeVersions}";
        gamesGrid.DataSource = state?.Games.Select(game => new
        {
            game.Id,
            game.Name,
            API = game.Api,
            Build = game.Build,
            Architecture = game.Executable == null ?
            "Select executable" :
            game.Architecture.ToString(),
            Executable = game.Executable ?? "Legacy profile"
        })
            .ToList();
        if (gamesGrid.Columns["Id"] is { } idColumn)
        {
            idColumn.Visible = false;
        }

        storageLabel.Text = state == null ?
            "Choose a central folder to get started." :
            "Central storage: " + state.Paths.Root + "\nShaders: " + state.Paths.Shaders +
            "\nProfiles: " + state.Paths.Profiles;
        if (state != null)
        {
            vulkanBuildSelector.SelectedItem = state.VulkanBuild;
            externalVulkanAppsList.Items.Clear();
            externalVulkanAppsList.Items.AddRange(state.ExternalVulkanApps.Cast<object>().ToArray());
        }
    }

    private static void OpenFolder(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe")
        {
            UseShellExecute = true,
            ArgumentList = { path }
        });

    private async Task OnInstallToGameAsync()
    {
        using var dialog = new GameDialog(manager.State!.VulkanBuild);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            await RunOperationAsync(
                "Installing game",
                cancellationToken => manager.InstallAsync(dialog.Result(), cancellationToken));
        }
    }

    private async Task OnRemoveSelectedAsync()
    {
        GameProfile game = GetSelectedGame();
        if (MessageBox.Show(
            this,
            "Remove managed links for " + game.Name + "? Presets and profile files are retained.",
            "Remove ReShade",
            MessageBoxButtons.OKCancel) == DialogResult.OK)
        {
            await RunOperationAsync(
                "Removing game links",
                cancellationToken => manager.RemoveAsync(game.Id, cancellationToken));
        }
    }

    private Task OnRepairAllAsync()
    {
        return RunOperationAsync(
            "Repairing managed installations",
            cancellationToken => manager.RepairAsync(progress, cancellationToken));
    }

    private Task OnOpenProfileAsync()
    {
        OpenFolder(GetSelectedGame().Directory);
        return Task.CompletedTask;
    }

    private Task OnRefreshCatalogAsync()
    {
        return RunOperationAsync("Refreshing catalog", async cancellationToken =>
        {
            catalog = await manager.Shaders.CatalogAsync(progress, cancellationToken);
            FilterPackages();
        });
    }

    private Task OnInstallUpdateSelectedAsync()
    {
        return UpdatePackagesAsync(false);
    }

    private Task OnUpdateInstalledPackagesAsync()
    {
        return UpdatePackagesAsync(true);
    }

    private Task OnUpdateReShadeAsync()
    {
        return RunOperationAsync(
            "Updating ReShade",
            cancellationToken => manager.UpdateRuntimeAsync(progress, cancellationToken));
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
        string executablePath = externalVulkanAppsList.SelectedItem as string ??
            throw new InvalidOperationException("Select an existing Vulkan consumer.");
        using var dialog = new GameDialog(
            manager.State!.VulkanBuild,
            new GameProfile
            {
                Name = Path.GetFileNameWithoutExtension(executablePath),
                Executable = executablePath,
                Api = RenderApi.Vulkan
            });
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            await RunOperationAsync(
                "Adopting Vulkan game",
                cancellationToken => manager.InstallAsync(dialog.Result(), cancellationToken));
        }
    }

    private async Task OnForgetSelectedRecordAsync()
    {
        string executablePath = externalVulkanAppsList.SelectedItem as string ??
            throw new InvalidOperationException("Select an existing Vulkan consumer.");
        if (MessageBox.Show(
            this,
            "Forget this consumer record? Its game configuration is retained. The shared " +
            "layer is removed if no recorded consumers remain.",
            "Forget record",
            MessageBoxButtons.OKCancel) == DialogResult.OK)
        {
            await RunOperationAsync(
                "Removing consumer record",
                cancellationToken => manager.ForgetExternalVulkanAppAsync(executablePath, cancellationToken));
        }
    }

    private async Task OnRelocateCentralFolderAsync()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose a separate, empty destination. Original data is retained.",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            await RunOperationAsync(
                "Relocating central storage",
                cancellationToken => manager.RelocateAsync(dialog.SelectedPath, cancellationToken));
        }
    }

    private Task OnOpenCentralFolderAsync()
    {
        OpenFolder(manager.State!.Paths.Root);
        return Task.CompletedTask;
    }

    private async Task OnRestoreAsync()
    {
        using var dialog = new BackupDialog(manager.Store.Backups);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            await RunOperationAsync(
                "Restoring previous state",
                cancellationToken => manager.RestoreToAsync(dialog.SelectedBackup, cancellationToken));
        }

        if (manager.State == null)
        {
            Close();
            return;
        }

        SynchronizePackageChoices();
        FilterPackages();
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
