using System.Diagnostics;
using System.Reflection;

namespace ReShadeCentralized;

public sealed partial class MainForm : Form
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
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells,
        RowHeadersVisible = false
    };

    private readonly PackagesGrid packageList = new();

    private readonly TextBox packageSearchBox = new()
    {
        PlaceholderText = "Search shader packages\u2026",
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
        AutoSize = true
    };

    private readonly Label statusLabel = new()
    {
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleLeft,
        Text = "Ready",
        Margin = new Padding(8)
    };

    private readonly Label runtimeStatusLabel = new()
    {
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        AccessibleName = "Installed ReShade",
        Margin = new Padding(16, 0, 16, 0)
    };

    private readonly DarkComboBox vulkanBuildSelector = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 260
    };

    private readonly VulkanGamesList vulkanGamesList = new()
    {
        Width = 800,
        Height = 105,
        HorizontalScrollbar = true
    };

    private readonly Label vulkanStatusLabel = new()
    {
        AutoSize = true,
        Dock = DockStyle.Fill,
        AccessibleName = "Vulkan registration status",
        Margin = Padding.Empty
    };

    private readonly Panel vulkanWarningIcon = new()
    {
        Size = new Size(16, 16),
        Margin = new Padding(0, 2, 8, 0),
        AccessibleName = "Warning"
    };

    private readonly FlatButton cancelButton = new("Cancel")
    {
        Enabled = false,
        Margin = Padding.Empty
    };

    private readonly HashSet<string> selectedPackageIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(Button Button, Func<bool> IsAvailable)> actionAvailability = [];
    private readonly IProgress<string> progress;
    private Button? applySelectionButton;
    private List<ShaderPackage> catalog = [];
    private CancellationTokenSource? operationCancellation;
    private string? lastVulkanInspectionError;

    public MainForm(string applicationDirectory, bool preview = false)
    {
        manager = new(StateStore.ForApplication(applicationDirectory), new WindowsLayerRegistry());
        Text = $"ReShade Centralized {AppVersion.Text}";
        ClientSize = new(1080, 760);
        MinimumSize = new(900, 650);
        StartPosition = FormStartPosition.CenterScreen;
        Font = DarkTheme.BodyFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        progress = new Progress<string>(WriteLog);
        manager.Warning += message => progress.Report(message);
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            Padding = new Padding(24)
        };
        root.RowStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.AutoSize));
        Controls.Add(root);
        root.Controls.Add(tabs);
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 12, 0, 0)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        statusLabel.Margin = Padding.Empty;
        footer.Controls.Add(statusLabel, 0, 0);
        footer.Controls.Add(runtimeStatusLabel, 1, 0);
        footer.Controls.Add(cancelButton, 2, 0);
        var helpButton = new FlatButton("?")
        {
            AutoSize = false,
            Size = new Size(36, 36),
            Padding = Padding.Empty,
            Margin = new Padding(8, 0, 0, 0),
            Dock = DockStyle.Fill,
            AccessibleName = "Help",
            AccessibleDescription = "Open the user guide. Shortcut: F1."
        };
        helpButton.Click += (_, _) => ShowHelp();
        footer.Controls.Add(helpButton, 3, 0);
        HelpRequested += (_, e) =>
        {
            ShowHelp();
            e.Handled = true;
        };
        root.Controls.Add(footer);
        cancelButton.Click += (_, _) => operationCancellation?.Cancel();
        BuildGames();
        BuildPackages();
        BuildSettings();
        tabs.Deselected += (_, e) =>
        {
            if (e.TabPageIndex == 1 && operationCancellation == null)
            {
                ResetPackageChoices();
            }
        };
        tabs.SelectedIndexChanged += (_, _) =>
        {
            if (tabs.SelectedIndex == 2 && operationCancellation == null)
            {
                RefreshVulkanStatus();
            }
        };
        vulkanBuildSelector.SelectedIndexChanged += (_, _) =>
        {
            if (tabs.SelectedIndex == 2 && operationCancellation == null)
            {
                RefreshVulkanStatus();
            }
        };
        TableLayoutPanel activity = CreatePage("Activity");
        activityLog.Font = DarkTheme.MonospaceFont;
        activityLog.WordWrap = false;
        activityLog.ScrollBars = ScrollBars.Both;
        activity.Controls.Add(activityLog, 0, 0);
        activity.SetRowSpan(activityLog, 3);
        BuildCredits();
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
                WriteLog("Cancelling the active operation before closing\u2026");
            }
        };
        FormClosed += (_, _) => manager.Dispose();
        DarkTheme.Apply(this);
        RefreshView();
        if (preview)
        {
            LoadCatalog();
        }
    }

    private void ShowHelp()
    {
        using var dialog = new HelpDialog();
        dialog.ShowDialog(this);
    }

    private Button CreateActionButton(
        string label,
        Func<Task> action,
        bool primary = false,
        Func<bool>? isAvailable = null)
    {
        var button = new FlatButton(label, primary);
        if (isAvailable != null)
        {
            actionAvailability.Add((button, isAvailable));
            button.Enabled = isAvailable();
        }

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

    private void UpdateActionAvailability()
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        foreach ((Button button, Func<bool> isAvailable) in actionAvailability)
        {
            button.Enabled = isAvailable();
        }

        if (applySelectionButton != null)
        {
            bool updateOnly = selectedPackageIds.Count > 0 && manager.State != null &&
                selectedPackageIds.SetEquals(manager.State.Packages.Select(package => package.Id));
            applySelectionButton.Text = updateOnly ? "Update selected" : "Apply selection";
        }
    }

    private async Task InitializeAsync()
    {
        await RunOperationAsync("Opening manager", _ => Task.Run(manager.Open));

        while (manager.State == null)
        {
            using var setup = new SetupDialog();
            if (setup.ShowDialog(this) != DialogResult.OK)
            {
                Close();
                return;
            }

            await RunOperationAsync(
                "Opening central storage",
                cancellationToken => manager.InitializeAsync(setup.CentralRoot, cancellationToken));
        }

        LoadCatalog();
        if (!manager.Shaders.HasCachedCatalog ||
            !File.Exists(Path.Combine(manager.Store.DirectoryPath, CompatibilityCatalog.CacheName)))
        {
            await RunOperationAsync("Loading catalogs", async cancellationToken =>
            {
                catalog = await manager.Shaders.CatalogAsync(progress, cancellationToken);
                ResetPackageChoices();
            });
        }
        RefreshView();
    }

    private void LoadCatalog()
    {
        catalog = manager.Shaders.CachedCatalog();
        SynchronizePackageChoices();
        FilterPackages();
    }

    private void BuildGames()
    {
        TableLayoutPanel layout = CreatePage("Games");
        FlowLayoutPanel commands = CreateCommandBar();
        commands.Controls.Add(CreateActionButton("Install game...", OnInstallToGameAsync, primary: true));
        commands.Controls.Add(CreateActionButton("Repair / edit...", EditSelectedAsync,
            isAvailable: () => gamesGrid.SelectedRows.Count > 0));
        commands.Controls.Add(CreateActionButton("Unregister", OnRemoveSelectedAsync,
            isAvailable: () => gamesGrid.SelectedRows.Count > 0));
        commands.Controls.Add(CreateActionButton("Repair all", OnRepairAllAsync,
            isAvailable: () => gamesGrid.Rows.Count > 0));
        commands.Controls.Add(CreateActionButton("Open profile", OnOpenProfileAsync,
            isAvailable: () => gamesGrid.SelectedRows.Count > 0));
        commands.Controls.Add(CreateActionButton("Open game folder", OnOpenGameFolderAsync,
            isAvailable: () => gamesGrid.SelectedRows.Count > 0 &&
                gamesGrid.SelectedRows[0].Cells["Executable"].Value is string executable &&
                Path.IsPathFullyQualified(executable)));
        gamesGrid.SelectionChanged += (_, _) => UpdateActionAvailability();
        gamesGrid.DataBindingComplete += (_, _) => UpdateActionAvailability();
        layout.Controls.Add(commands, 0, 0);
        layout.Controls.Add(gamesGrid, 0, 1);
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
        return RequireState().Games.SingleOrDefault(game => game.Id == id) ??
            throw new InvalidOperationException("No game is selected. Open Games and select a row. " +
                "To add a game first, choose Install game.");
    }

    private ManagerState RequireState() => manager.State ??
        throw new InvalidOperationException("No central folder is configured. Restart ReShade Centralized " +
            "and complete Setup before using this action.");

    private async Task EditSelectedAsync()
    {
        GameProfile game = GetSelectedGame();
        ManagerState state = RequireState();
        using var dialog = new GameDialog(state.VulkanBuild, game, state.Games, state: state);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            await RunOperationAsync(
                "Repairing game",
                cancellationToken => manager.InstallAsync(
                    dialog.Result(), cancellationToken, dialog.ReviewedPlan, dialog.ReplacementConfirmed));
        }
    }

    private void BuildPackages()
    {
        TableLayoutPanel layout = CreatePage("Shader packages");
        packageSearchBox.Dock = DockStyle.Fill;
        packageSearchBox.Margin = new Padding(0, 0, 0, 12);
        packageSearchBox.AccessibleName = "Search shader packages";
        layout.Controls.Add(packageSearchBox, 0, 0);
        packageList.Font = DarkTheme.BodyFont;
        packageList.Margin = Padding.Empty;
        layout.Controls.Add(packageList, 0, 1);
        FlowLayoutPanel commands = CreateCommandBar();
        commands.WrapContents = true;
        commands.Margin = new Padding(0, 12, 0, 0);
        applySelectionButton = CreateActionButton(
            "Apply selection", OnApplyShaderSelectionAsync, primary: true,
            isAvailable: () => selectedPackageIds.Count > 0 || manager.State is { Packages.Count: > 0 });
        commands.Controls.Add(applySelectionButton);
        commands.Controls.Add(CreateActionButton("Refresh catalog", OnRefreshCatalogAsync));
        commands.Controls.Add(CreateActionButton("Purge shader folder...", OnPurgeShaderFolderAsync,
            isAvailable: () => manager.State != null));
        layout.Controls.Add(commands, 0, 2);
        packageSearchBox.TextChanged += (_, _) => FilterPackages();
        packageList.PackageChecked += (package, selected) =>
        {
            if (selected)
            {
                selectedPackageIds.Add(package.Id);
            }
            else
            {
                selectedPackageIds.Remove(package.Id);
            }
            ExpandShaderDependencies();
            packageList.SetChecks(selectedPackageIds, AvailablePackages());
            UpdateActionAvailability();
        };
    }

    private List<ShaderPackage> AvailablePackages() => catalog.Concat(
        (manager.State?.Packages ?? []).Where(installed => !catalog.Any(package => package.Id == installed.Id))
            .Select(Manager.PackageDescriptor)).ToList();

    private void ExpandShaderDependencies()
    {
        List<ShaderPackage> packages = AvailablePackages();
        ShaderPackage[] selected = packages.Where(package => selectedPackageIds.Contains(package.Id)).ToArray();
        foreach (ShaderPackage dependency in ShaderService.WithDependencies(selected, packages, (_, _) => { }))
        {
            selectedPackageIds.Add(dependency.Id);
        }
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
            foreach (InstalledPackage installed in manager.State?.Packages ?? [])
            {
                selectedPackageIds.Add(installed.Id);
            }
            foreach (ShaderPackage package in catalog.Where(package => package.Required || package.Enabled))
            {
                selectedPackageIds.Add(package.Id);
            }
        }
        ExpandShaderDependencies();
    }

    private void FilterPackages()
    {
        List<ShaderPackage> packages = AvailablePackages();
        packageList.SetPackages(packages.Where(package =>
            $"{package.Name} {package.Description}".Contains(packageSearchBox.Text, StringComparison.OrdinalIgnoreCase)),
            selectedPackageIds, manager.State);
        packageList.SetChecks(selectedPackageIds, packages);
        UpdateActionAvailability();
    }

    private void ResetPackageChoices()
    {
        packageList.CancelEdit();
        SynchronizePackageChoices();
        FilterPackages();
    }

    private async Task ApplyPackagesAsync()
    {
        packageList.EndEdit();
        List<ShaderPackage> packages = AvailablePackages();
        ShaderSelectionPlan plan = manager.CreateShaderSelectionPlan(
            packages.Where(package => selectedPackageIds.Contains(package.Id)).ToArray(), packages);
        try
        {
            await RunOperationAsync("Applying shader selection", async cancellationToken =>
            {
                PackageUpdateReport report = await manager.ApplyShaderSelectionAsync(
                    plan, packages, progress, cancellationToken, removalsConfirmed: true);
                WriteLog(report.Summary);
                if (report.FailedCount > 0)
                {
                    tabs.SelectedIndex = 3;
                    MessageBox.Show(this, report.Summary +
                        "\n\nCompleted changes were kept. Check Activity for failures, then apply the selection again.",
                        "Shader packages", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }, reportCompletion: false);
        }
        finally
        {
            FilterPackages();
        }
    }

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

    private TableLayoutPanel CreatePage(string tabName)
    {
        var page = new TabPage(tabName) { Padding = new Padding(20) };
        tabs.TabPages.Add(page);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.Controls.Add(layout);
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

    private async Task<bool> RunOperationAsync(
        string description,
        Func<CancellationToken, Task> action,
        bool reportCompletion = true)
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
            if (reportCompletion)
            {
                WriteLog("Finished: " + description);
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            WriteLog("Cancelled. Completed work was kept; unfinished changes were rolled back.");
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
        WriteLog("Failed: " + error);
        MessageBox.Show(
            this,
            UserErrors.Describe(error) + "\n\nTechnical details are in Activity.",
            error is InvalidOperationException or ArgumentException ? "Action unavailable" : "Operation failed",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private void RefreshView()
    {
        ManagerState? state = manager.State;
        Dictionary<RuntimeBuild, string> runtimeVersions = Enum.GetValues<RuntimeBuild>()
            .ToDictionary(build => build, build => GetInstalledRuntimeVersion(state, build));
        versionsLabel.Text = string.Join(
            "   \u2022   ",
            runtimeVersions.Select(item => $"{DisplayNames.For(item.Key)}: {item.Value}"));
        runtimeStatusLabel.Text = runtimeVersions.Values.All(version => version == "Not installed")
            ? "ReShade: Not installed"
            : $"ReShade: {runtimeVersions[RuntimeBuild.Standard]}   |   " +
                $"Add-on support: {runtimeVersions[RuntimeBuild.FullAddon]}";
        gamesGrid.DataSource = state?.Games.Select(game => new
        {
            game.Id,
            InstallationWarning = DeploymentService.GetInstallationWarning(game),
            Name = Path.GetFileName(game.Executable),
            API = game.Api == RenderApi.DXGI ? "DXGI" : DisplayNames.For(game.Api),
            Build = game.Build == RuntimeBuild.FullAddon ? "Standard + add-on" : DisplayNames.For(game.Build),
            Architecture = game.Architecture == CpuArchitecture.X86 ? "32-bit" : "64-bit",
            game.Executable
        })
            .ToList();
        if (gamesGrid.Columns["Id"] is { } idColumn)
        {
            idColumn.Visible = false;
        }

        storageLabel.Text = state?.Paths.Root ?? "Not configured";
        if (state != null)
        {
            vulkanBuildSelector.SelectedItem = state.VulkanBuild;
        }

        RefreshVulkanStatus();
        FilterPackages();
        UpdateActionAvailability();
    }

    private void RefreshVulkanStatus()
    {
        VulkanInspection inspection = manager.InspectVulkan();
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

    private static string GetInstalledRuntimeVersion(ManagerState? state, RuntimeBuild build)
    {
        if (state == null)
        {
            return "Not installed";
        }

        string directory = state.Paths.Binaries(build);
        string[] binaries = [Path.Combine(directory, "ReShade32.dll"), Path.Combine(directory, "ReShade64.dll")];
        if (!binaries.Any(File.Exists))
        {
            return "Not installed";
        }

        if (!binaries.All(File.Exists))
        {
            return "Incomplete";
        }

        try
        {
            string[] versions = binaries.Select(path =>
                FileVersionInfo.GetVersionInfo(path).ProductVersion?.Split('+')[0] ?? "Unknown version")
                .Distinct()
                .ToArray();
            return versions.Length == 1 ? versions[0] : "Version mismatch";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "Unavailable";
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
        ManagerState state = RequireState();
        using var dialog = new GameDialog(state.VulkanBuild, managedGames: state.Games, state: state);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            await RunOperationAsync(
                "Installing game",
                cancellationToken => manager.InstallAsync(
                    dialog.Result(), cancellationToken, dialog.ReviewedPlan, dialog.ReplacementConfirmed));
        }
    }

    private async Task OnRemoveSelectedAsync()
    {
        GameProfile game = GetSelectedGame();
        if (MessageBox.Show(
            this,
            "Unregister " + game.Name + "? Presets, profile files, and local shaders are kept. " +
                "Files changed by other tools are left in place.",
            "Unregister ReShade",
            MessageBoxButtons.OKCancel) == DialogResult.OK)
        {
            await RunOperationAsync(
                "Unregistering game",
                async cancellationToken =>
                {
                    UnregisterResult result = await manager.UnregisterAsync(game.Id, cancellationToken);
                    foreach (string path in result.PreservedFiles)
                    {
                        WriteLog("Preserved externally changed file: " + path);
                    }
                });
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

    private Task OnOpenGameFolderAsync()
    {
        GameProfile game = GetSelectedGame();
        string directory = Path.GetDirectoryName(game.Executable)!;
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"The game folder no longer exists: {directory}. " +
                "Choose Games > Repair / edit and select the game's current executable.");
        }

        OpenFolder(directory);
        return Task.CompletedTask;
    }

    private Task OnRefreshCatalogAsync()
    {
        ResetPackageChoices();
        return RunOperationAsync("Refreshing catalog", async cancellationToken =>
        {
            catalog = await manager.Shaders.CatalogAsync(progress, cancellationToken);
            ResetPackageChoices();
        });
    }

    private Task OnApplyShaderSelectionAsync()
    {
        return ApplyPackagesAsync();
    }

    private async Task OnPurgeShaderFolderAsync()
    {
        string root = RequireState().Paths.ShaderRoot;
        ResetPackageChoices();
        DialogResult answer = MessageBox.Show(this,
            "Delete all central shaders and textures, including custom and unmanaged files?\n\n" + root +
            "\n\nPresets, profiles and game-local shaders will be kept. Close affected games first. " +
            "After purging, choose Apply selection to reinstall your selected packages.",
            "Purge shader folder", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        await RunOperationAsync("Purging central shader folder",
            cancellationToken => manager.PurgeShaderFolderAsync(progress, cancellationToken));
        ResetPackageChoices();
    }

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
        ManagerState state = RequireState();
        var game = new GameProfile
        {
            Name = Path.GetFileName(executablePath),
            Executable = executablePath,
            Api = RenderApi.Vulkan
        };
        using var dialog = new GameDialog(
            state.VulkanBuild, game, state.Games, state: state);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            await RunOperationAsync(
                "Adopting Vulkan game",
                cancellationToken => manager.InstallAsync(
                    dialog.Result(), cancellationToken, dialog.ReviewedPlan, dialog.ReplacementConfirmed));
        }
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
