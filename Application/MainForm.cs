using System.Reflection;

namespace ReShadeCentralized;

public sealed partial class MainForm : Form {
    private static readonly string AppVersion =
        typeof(MainForm).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+')[0];

    private readonly CentralStorage storage;
    private readonly Downloads downloads = new();
    private readonly VulkanService vulkan;
    private readonly GameLibrary games;
    private readonly RuntimeInstaller runtimes;
    private readonly ShaderCatalog shaderCatalog;
    private readonly ShaderLibrary shaders;
    private readonly NavigationTabs tabs = new() {
        Dock = DockStyle.Fill
    };

    private readonly TextBox activityLog = new() {
        Multiline = true,
        ReadOnly = true,
        Dock = DockStyle.Fill
    };

    private readonly Label statusLabel = new() {
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = Padding.Empty
    };

    private readonly List<(Button Button, Func<bool> IsAvailable)> actionAvailability = [];
    private readonly IProgress<string> progress;

    public MainForm(string applicationDirectory) {
        var registry = new WindowsLayerRegistry();
        storage = new(StateStore.ForApplication(applicationDirectory), registry);
        vulkan = new(registry);
        games = new(storage, vulkan);
        runtimes = new(storage, vulkan, downloads);
        shaderCatalog = new(downloads, storage.Store);
        shaders = new(storage, downloads);

        using (Stream icon = typeof(MainForm).Assembly.GetManifestResourceStream("ReShadeCentralized.AppIcon.ico")!) {
            Icon = new Icon(icon);
        }

        Text = $"ReShade Centralized {AppVersion}";
        ClientSize = new(1080, 760);
        MinimumSize = new(900, 650);
        StartPosition = FormStartPosition.CenterScreen;
        Font = DarkTheme.BodyFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        progress = new Progress<string>(WriteLog);
        storage.Warning += message => progress.Report(message);

        var root = new TableLayoutPanel {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            Padding = new Padding(24)
        };
        root.RowStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.AutoSize));
        Controls.Add(root);
        root.Controls.Add(tabs);

        var footer = new TableLayoutPanel {
            Dock = DockStyle.Fill,
            ColumnCount = 6,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 12, 0, 0)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int column = 1; column < footer.ColumnCount; column++) {
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        }

        footer.Controls.Add(statusLabel, 0, 0);
        BuildRuntimeUpdateNotice(footer);
        footer.Controls.Add(runtimeStatusLabel, 2, 0);
        FlowLayoutPanel storageCommands = CreateCommandBar();
        storageCommands.Controls.Add(cancelButton);
        storageCommands.Controls.Add(chooseStorageButton);
        footer.Controls.Add(storageCommands, 3, 0);

        var helpButton = new FlatButton("?") {
            AutoSize = false,
            Size = new Size(36, 36),
            Padding = Padding.Empty,
            Margin = new Padding(8, 0, 0, 0),
            Dock = DockStyle.Fill,
            AccessibleName = "Help",
            AccessibleDescription = "Open the user guide. Shortcut: F1."
        };
        helpButton.Click += (_, _) => ShowHelp();
        footer.Controls.Add(runtimeUpdateButton, 4, 0);
        footer.Controls.Add(helpButton, 5, 0);

        HelpRequested += (_, e) => {
            ShowHelp();
            e.Handled = true;
        };
        root.Controls.Add(footer);

        cancelButton.Click += async (_, _) => {
            if (storageLoadFailed && operationCancellation == null)
                await InitializeAsync();
            else
                operationCancellation?.Cancel();
        };
        chooseStorageButton.Click += async (_, _) => await ChooseStorageAsync();
        BuildGames();
        BuildPackages();
        BuildSettings();

        async void RefreshVulkanSettings(object? sender, EventArgs e) => await RefreshVulkanStatusAsync();
        tabs.SelectedIndexChanged += RefreshVulkanSettings;
        vulkanBuildSelector.SelectedIndexChanged += RefreshVulkanSettings;
        TableLayoutPanel activity = CreatePage("Activity");
        activityLog.Font = DarkTheme.MonospaceFont;
        activityLog.WordWrap = false;
        activityLog.ScrollBars = ScrollBars.Both;
        activity.Controls.Add(activityLog, 0, 0);
        activity.SetRowSpan(activityLog, 3);
        BuildCredits();
        Shown += async (_, _) => await InitializeAsync();

        FormClosing += (_, e) => {
            if (!closeAfterOperation || operationCancellation != null)
                shutdownDeadline.Request();

            if (operationCancellation != null) {
                e.Cancel = true;
                closeAfterOperation = true;
                Text = "Closing ReShade Centralized - close again to exit immediately";
                SetStorageControlsLocked(true);
                tabs.Enabled = false;
                cancelButton.Enabled = false;
                chooseStorageButton.Visible = false;
                operationCancellation.Cancel();
                storageLoading.Report(new("Closing; close again to exit immediately"));
                WriteLog("Closing; close again to exit immediately. Waiting briefly for active work to stop.");
            }
        };
        FormClosed += (_, _) => {
            Icon?.Dispose();
            storage.Dispose();
            downloads.Dispose();
            shutdownDeadline.Dispose();
        };
        DarkTheme.Apply(this);
        SetStorageControlsLocked(true);
        statusLabel.Text = "Loading central storage";
        storageLoading.BeginLoading();
    }

    private void ShowHelp() {
        if (closeAfterOperation)
            return;

        using var dialog = new HelpDialog();
        dialog.ShowDialog(this);
    }

    private Button CreateActionButton(
        string label,
        Func<Task> action,
        bool primary = false,
        Func<bool>? isAvailable = null) {
        var button = new FlatButton(label, primary);

        if (isAvailable != null) {
            actionAvailability.Add((button, isAvailable));
            button.Enabled = isAvailable();
        }

        button.Click += async (_, _) => {
            try {
                await action();
            } catch (Exception e) {
                ReportError(e);
            }
        };
        return button;
    }

    private void UpdateActionAvailability() {
        if (IsDisposed || Disposing)
            return;

        foreach ((Button button, Func<bool> isAvailable) in actionAvailability) {
            button.Enabled = isAvailable();
        }

        if (applySelectionButton != null) {
            bool updateOnly = selectedPackageIds.Count > 0 && storage.State != null
                && selectedPackageIds.SetEquals(storage.State.Packages.Select(package => package.Id));
            applySelectionButton.Text = updateOnly ? "Update selected" : "Apply selection";
        }
    }

    private TableLayoutPanel CreatePage(string tabName) {
        var page = new TabPage(tabName) { Padding = new Padding(20) };
        tabs.TabPages.Add(page);

        var layout = new TableLayoutPanel {
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

    private static FlowLayoutPanel CreateCommandBar() => new() {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        WrapContents = false,
        Dock = DockStyle.Fill,
        Margin = Padding.Empty
    };

    private static TableLayoutPanel CreateSection(TableLayoutPanel parent, string title) {
        var section = new TableLayoutPanel {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(18),
            Margin = new Padding(0, 0, 0, 16),
            BackColor = DarkTheme.Raised
        };
        section.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        section.Paint += (_, e) => {
            using var border = new Pen(DarkTheme.Border);
            e.Graphics.DrawRectangle(border, 0, 0, section.Width - 1, section.Height - 1);
        };

        parent.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        parent.Controls.Add(section, 0, parent.RowCount++);
        AddSectionRow(section, new Label {
            Text = title,
            Font = new Font(DarkTheme.BodyFont, FontStyle.Bold),
            AutoSize = true
        });
        return section;
    }

    private static void AddSectionRow(TableLayoutPanel section, Control control) {
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(0, 0, 0, 12);
        section.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        section.Controls.Add(control, 0, section.RowCount++);
    }
}
