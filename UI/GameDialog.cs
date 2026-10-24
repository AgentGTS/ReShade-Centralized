using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Text.Json;

namespace ReShadeCentralized;

internal sealed class GameDialog : Form
{
    private const string ApiPlaceholder = "-- Select graphics API --";
    private const string BuildPlaceholder = "-- Select ReShade build --";
    private const int MinimumClientWidth = 760;
    private const int MinimumClientHeight = 320;
    private const int MaximumWarningLines = 10;
    private readonly LinkLabel detectionHint = new()
    {
        AutoSize = true,
        Anchor = AnchorStyles.Top | AnchorStyles.Left,
        Margin = Padding.Empty,
        AccessibleName = "Graphics API detection",
        LinkBehavior = LinkBehavior.HoverUnderline,
        LinkColor = DarkTheme.Muted,
        ActiveLinkColor = DarkTheme.Accent,
        VisitedLinkColor = DarkTheme.Muted
    };
    private readonly ApiCheckIcon detectionIcon = new()
    {
        Margin = new Padding(0, 1, 8, 0)
    };
    private readonly TableLayoutPanel detectionPanel = new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        ColumnCount = 2,
        Dock = DockStyle.Fill,
        Margin = Padding.Empty,
        Visible = false
    };
    private readonly TextBox executable = new()
    {
        AccessibleName = "Game executable"
    };
    private readonly DarkComboBox api = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList
    };

    private readonly DarkComboBox build = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList
    };

    private readonly TableLayoutPanel layout;
    private readonly TableLayoutPanel frame;
    private readonly Panel contentViewport;
    private readonly Control buttons;
    private readonly Label buildLabel;
    private readonly Button installButton;
    private readonly IReadOnlyList<OwnedLink> managedLinks;
    private readonly ManagerState state;
    public InstallationPlan? ReviewedPlan { get; private set; }
    public bool ReplacementConfirmed => replaceConfirmation.Checked;
    private readonly ThemedCheckBox replaceConfirmation = new()
    {
        Text = "Replace the listed files or links; keep existing link targets",
        ForeColor = DarkTheme.Text,
        CheckAlign = ContentAlignment.MiddleRight,
        AutoSize = true,
        Visible = false,
        Margin = new Padding(0, 0, 24, 0)
    };
    private readonly TableLayoutPanel warningPanel = new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Dock = DockStyle.Fill,
        ColumnCount = 2,
        Margin = Padding.Empty,
        Visible = false
    };
    private readonly Panel warningViewport = new()
    {
        Dock = DockStyle.Fill,
        Margin = Padding.Empty
    };

    private readonly Label warningText = new()
    {
        AutoSize = true,
        UseMnemonic = false,
        UseCompatibleTextRendering = true,
        Dock = DockStyle.Top,
        Margin = Padding.Empty,
        AccessibleName = "Installation changes"
    };

    private readonly RuntimeBuild global;
    private readonly GameProfile original;
    private readonly Func<string, CancellationToken, Task<RenderApi?>> apiDetector;
    private CancellationTokenSource? detectionCancellation;
    private int executableVersion;
    private RuntimeBuild? nonVulkanBuild;
    private bool usingGlobalBuild;
    private bool dialogClosing;
    private bool sizingDialog;
    private Font? confirmationFont;

    public GameProfile Result()
    {
        if (api.SelectedItem is not RenderApi selectedApi || build.SelectedItem is not RuntimeBuild selectedBuild)
        {
            throw new InvalidOperationException("Select a graphics API and ReShade build before installing.");
        }

        GameProfile game = JsonSerializer.Deserialize<GameProfile>(
            JsonSerializer.Serialize(original, ManagerState.Json),
            ManagerState.Json)!;
        game.Executable = executable.Text.Trim();
        game.Name = Path.GetFileName(game.Executable);
        game.Api = selectedApi;
        game.Build = game.Api == RenderApi.Vulkan ? global : selectedBuild;
        return game;
    }

    public GameDialog(
        RuntimeBuild global,
        GameProfile? game = null,
        IReadOnlyList<GameProfile>? managedGames = null,
        Func<string, CancellationToken, Task<RenderApi?>>? apiDetector = null,
        ManagerState? state = null)
    {
        this.global = global;
        this.apiDetector = apiDetector ?? ((path, cancellationToken) =>
            Task.Run(() => GraphicsApiDetector.Detect(path, cancellationToken), cancellationToken));
        original = game ?? new();
        this.state = state ?? new ManagerState
        {
            Paths = CentralPaths.At(Path.Combine(AppContext.BaseDirectory, "Central")),
            Games = managedGames?.ToList() ?? (game == null ? [] : [game])
        };
        managedLinks = (managedGames ?? []).SelectMany(profile => profile.Links).Concat(original.Links)
            .DistinctBy(link => link.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        Text = game == null ? "Install game" : "Repair / edit game";
        ClientSize = new(MinimumClientWidth, MinimumClientHeight);
        layout = DialogLayout.Table(this);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        SizeGripStyle = SizeGripStyle.Hide;
        layout.Dock = DockStyle.Top;
        layout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        layout.Padding = Padding.Empty;
        Controls.Remove(layout);
        frame = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(24),
            Margin = Padding.Empty
        };
        frame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        frame.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        frame.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        contentViewport = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        contentViewport.Controls.Add(layout);
        frame.Controls.Add(contentViewport, 0, 0);
        Controls.Add(frame);
        executable.Text = original.Executable;
        api.Items.Add(ApiPlaceholder);
        api.Items.AddRange(Enum.GetValues<RenderApi>().Cast<object>().ToArray());
        api.SelectedItem = !string.IsNullOrWhiteSpace(game?.Executable) ? original.Api : ApiPlaceholder;
        build.Items.Add(BuildPlaceholder);
        build.Items.AddRange(Enum.GetValues<RuntimeBuild>().Cast<object>().ToArray());
        build.SelectedItem = game == null ? BuildPlaceholder : original.Build;
        build.Format += (_, e) =>
        {
            if (api.SelectedItem is RenderApi.Vulkan && e.ListItem is RuntimeBuild runtimeBuild)
            {
                e.Value = $"{DisplayNames.For(runtimeBuild)} (Global)";
            }
        };

        detectionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 24));
        detectionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        detectionPanel.Controls.Add(detectionIcon, 0, 0);
        detectionPanel.Controls.Add(detectionHint, 1, 0);
        DialogLayout.Row(layout, "", detectionPanel, fullWidth: false, margin: Padding.Empty);
        Control browse = DialogLayout.Browse(executable, true, fileSelected: DetectApiAsync);
        DialogLayout.Row(layout, "Executable", browse, margin: new Padding(0, 8, 0, 0));
        warningPanel.ColumnStyles.Add(new(SizeType.Absolute, 24));
        warningPanel.ColumnStyles.Add(new(SizeType.Percent, 100));
        var warningIcon = new Panel
        {
            Size = new Size(16, 16),
            Margin = new Padding(0, 2, 8, 0),
            AccessibleName = "Installation information",
            AccessibleRole = AccessibleRole.Graphic
        };
        warningIcon.Paint += (_, e) =>
        {
            float scale = Math.Min(warningIcon.ClientSize.Width, warningIcon.ClientSize.Height) / 16f;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.ScaleTransform(scale, scale);
            Color color = warningText.ForeColor == DarkTheme.Warning ? DarkTheme.Warning : DarkTheme.Accent;
            using var pen = new Pen(color, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var fill = new SolidBrush(color);
            e.Graphics.DrawEllipse(pen, 1.5f, 1.5f, 13, 13);
            e.Graphics.FillEllipse(fill, 7.2f, 4, 1.6f, 1.6f);
            e.Graphics.DrawLine(pen, 8, 7, 8, 11.5f);
        };
        warningPanel.Controls.Add(warningIcon, 0, 0);
        warningViewport.Controls.Add(warningText);
        warningPanel.Controls.Add(warningViewport, 1, 0);
        DialogLayout.Row(layout, "", warningPanel, fullWidth: false, margin: new Padding(0, 8, 0, 0));
        DialogLayout.Row(layout, "Graphics API", api, margin: new Padding(0, 16, 0, 0));
        buildLabel = DialogLayout.Row(layout, "Build", build, margin: new Padding(0, 16, 0, 0))!;
        installButton = DialogLayout.Buttons(layout, this, game == null ? "Install" : "Repair");
        buttons = installButton.Parent!;
        layout.Controls.Remove(buttons);
        layout.RowStyles.RemoveAt(layout.RowStyles.Count - 1);
        layout.RowCount--;
        frame.Controls.Add(buttons, 0, 1);
        buttons.Margin = new Padding(0, 24, 0, 0);
        foreach (Control button in buttons.Controls)
        {
            button.Margin = button == installButton ? Padding.Empty : new Padding(0, 0, 8, 0);
        }
        buttons.Controls.Add(replaceConfirmation);
        buttons.Layout += (_, _) =>
        {
            int top = Math.Max(0, (installButton.Height - replaceConfirmation.PreferredSize.Height) / 2);
            var margin = new Padding(0, top, LogicalToDeviceUnits(24), 0);
            if (replaceConfirmation.Margin != margin)
            {
                replaceConfirmation.Margin = margin;
            }
        };
        detectionHint.LinkClicked += async (_, _) => await DetectApiAsync();
        detectionHint.MouseEnter += (_, _) => HighlightCheckLink(true);
        detectionHint.MouseLeave += (_, _) => HighlightCheckLink(false);
        api.SelectedIndexChanged += (_, _) => UpdateBuild();
        build.SelectedIndexChanged += (_, _) => UpdateInstallationNotice();
        replaceConfirmation.CheckedChanged += (_, _) => UpdateInstallationNotice();
        executable.TextChanged += (_, _) =>
        {
            CancelApiDetection();
            SetDetectionHint("Check graphics API", ApiCheckState.Ready);
            UpdateInstallationNotice();
        };
        if (!string.IsNullOrWhiteSpace(game?.Executable) && original.Api == RenderApi.Vulkan)
        {
            build.SelectedItem = global;
        }
        UpdateBuild();
        SetDetectionHint("Check graphics API", ApiCheckState.Ready);
    }

    private void UpdateBuild()
    {
        bool vulkan = api.SelectedItem is RenderApi.Vulkan;
        if (vulkan && !usingGlobalBuild)
        {
            nonVulkanBuild = build.SelectedItem as RuntimeBuild?;
            build.SelectedItem = global;
        }
        else if (!vulkan && usingGlobalBuild)
        {
            build.SelectedItem = nonVulkanBuild.HasValue ? nonVulkanBuild.Value : BuildPlaceholder;
        }

        usingGlobalBuild = vulkan;
        build.Enabled = !vulkan;
        buildLabel.Text = vulkan ? "Global build" : "Build";
        UpdateInstallationNotice();
    }

    private async Task DetectApiAsync()
    {
        if (dialogClosing || IsDisposed || Disposing)
        {
            return;
        }

        CancelApiDetection();
        string? path = PortableExecutable.GetValidatedGamePath(executable.Text);
        if (path == null)
        {
            SetDetectionHint("Choose a valid local x86/x64 .exe outside Windows, then check again.", ApiCheckState.Warning);
            return;
        }

        using var cancellation = new CancellationTokenSource();
        detectionCancellation = cancellation;
        int currentExecutable = executableVersion;
        SetDetectionHint("Checking graphics API...", ApiCheckState.Checking);
        try
        {
            RenderApi? detected = await apiDetector(path, cancellation.Token);
            if (!CanDisplayDetection(currentExecutable, cancellation.Token))
            {
                return;
            }

            if (detected.HasValue)
            {
                SetDetectionHint($"Recommended API: {DisplayNames.For(detected.Value)}.", ApiCheckState.Detected);
            }
            else
            {
                SetDetectionHint("Could not identify the graphics API. Select it below.", ApiCheckState.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            // A changed path or closed dialog makes this result obsolete.
        }
        catch (Exception error)
        {
            // Recommendations are optional and must not interrupt a confirmed installation.
            Trace.TraceWarning($"Graphics API recommendation failed: {error}");
            if (CanDisplayDetection(currentExecutable, cancellation.Token))
            {
                SetDetectionHint("Could not check the graphics API. Select it manually.", ApiCheckState.Warning);
            }
        }
        finally
        {
            if (ReferenceEquals(detectionCancellation, cancellation))
            {
                detectionCancellation = null;
            }
        }
    }

    private bool CanDisplayDetection(int version, CancellationToken cancellationToken) =>
        !dialogClosing && !IsDisposed && !Disposing && !cancellationToken.IsCancellationRequested &&
        version == executableVersion;

    private void CancelApiDetection()
    {
        executableVersion++;
        detectionCancellation?.Cancel();
    }

    private void HighlightCheckLink(bool highlighted)
    {
        if (detectionIcon.State != ApiCheckState.Ready)
        {
            return;
        }

        Color color = highlighted ? DarkTheme.Accent : DarkTheme.Muted;
        detectionHint.LinkColor = color;
        detectionIcon.ForeColor = color;
    }

    private void SetDetectionHint(string text, ApiCheckState state)
    {
        detectionHint.Links.Clear();
        detectionHint.Text = text;
        if (state == ApiCheckState.Ready)
        {
            detectionHint.Links.Add(0, text.Length);
        }
        Color color = state switch
        {
            ApiCheckState.Detected => DarkTheme.Accent,
            ApiCheckState.Warning => DarkTheme.Warning,
            _ => DarkTheme.Muted
        };
        detectionHint.LinkColor = DarkTheme.Muted;
        detectionHint.ForeColor = color;
        detectionIcon.ForeColor = color;
        detectionIcon.State = state;
        detectionPanel.Visible = text.Length > 0;
        UpdateDialogSize();
    }

    private void UpdateInstallationNotice()
    {
        ExistingInstallationNotice? notice;
        InstallationPlan? plan = null;
        try
        {
            if (PortableExecutable.GetValidatedGamePath(executable.Text.Trim()) != null &&
                api.SelectedItem is RenderApi && build.SelectedItem is RuntimeBuild)
            {
                GameProfile game = Result();
                game.Directory = ProfilePaths.DirectoryFor(state, game);
                plan = DeploymentService.CreatePlan(state, game);
                notice = new(plan.Describe(includePaths: false), plan.Conflict != null);
            }
            else
            {
                notice = DeploymentService.InspectExistingInstallation(
                    executable.Text.Trim(), api.SelectedItem as RenderApi?, managedLinks, includePaths: false);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            ArgumentException or Win32Exception)
        {
            notice = new("Cannot review this installation. Check folder permissions and close programs using " +
                "its ReShade files, then select the executable again.", true);
        }

        if (ReviewedPlan?.Fingerprint != plan?.Fingerprint)
        {
            // An acknowledgement applies only to the exact files and destinations displayed.
            replaceConfirmation.Checked = false;
        }

        ReviewedPlan = plan;
        replaceConfirmation.Visible = plan?.RequiresConfirmation == true && plan.Conflict == null;
        string path = executable.Text.Trim();
        installButton.Enabled = PortableExecutable.GetValidatedGamePath(path) != null &&
            api.SelectedItem is RenderApi && build.SelectedItem is RuntimeBuild &&
            notice?.BlocksInstallation != true && plan != null &&
            (!plan.RequiresConfirmation || replaceConfirmation.Checked);
        warningText.Text = (notice?.Message ?? "").ReplaceLineEndings(Environment.NewLine);
        warningText.ForeColor = notice?.BlocksInstallation == true || plan?.RequiresConfirmation == true ?
            DarkTheme.Warning : DarkTheme.Muted;
        warningPanel.Visible = notice != null;
        warningPanel.Invalidate(true);
        UpdateDialogSize();
    }

    private void UpdateDialogSize()
    {
        if (sizingDialog || buttons == null)
        {
            return;
        }

        sizingDialog = true;
        try
        {
            Rectangle workingArea = Screen.FromControl(this).WorkingArea;
            Size chrome = SizeFromClientSize(Size.Empty);
            int screenMargin = LogicalToDeviceUnits(16);
            int maxWidth = Math.Max(1, workingArea.Width - chrome.Width - (screenMargin * 2));
            int maxHeight = Math.Max(1, workingArea.Height - chrome.Height - (screenMargin * 2));
            MinimumSize = SizeFromClientSize(new Size(
                Math.Min(LogicalToDeviceUnits(MinimumClientWidth), maxWidth),
                Math.Min(LogicalToDeviceUnits(MinimumClientHeight), maxHeight)));
            int labelWidth = layout.Controls.OfType<Label>()
                .Select(label => label.GetPreferredSize(Size.Empty).Width + label.Margin.Right)
                .DefaultIfEmpty(0).Max();
            layout.ColumnStyles[0].Width = Math.Max(LogicalToDeviceUnits(145), labelWidth);
            int choiceWidth = api.Items.Cast<object>().Select(api.GetItemText)
                .Concat(build.Items.Cast<object>().Select(build.GetItemText))
                .Select(text => TextRenderer.MeasureText(text, Font).Width).DefaultIfEmpty(0).Max();
            int preferredWidth = frame.Padding.Horizontal + (int)layout.ColumnStyles[0].Width +
                choiceWidth + LogicalToDeviceUnits(48);
            preferredWidth = Math.Max(preferredWidth, buttons.PreferredSize.Width + frame.Padding.Horizontal);
            int width = Math.Min(Math.Max(LogicalToDeviceUnits(MinimumClientWidth), preferredWidth), maxWidth);
            int preferredHeight;
            contentViewport.AutoScroll = false;
            contentViewport.AutoScrollPosition = Point.Empty;
            do
            {
                ClientSize = new Size(width, ClientSize.Height);
                frame.PerformLayout();
                SizeContent();
                preferredHeight = layout.PreferredSize.Height + buttons.PreferredSize.Height +
                    buttons.Margin.Vertical + frame.Padding.Vertical;
                if (preferredHeight <= maxHeight || width == maxWidth)
                {
                    break;
                }

                width = Math.Min(width + LogicalToDeviceUnits(80), maxWidth);
            }
            while (true);

            if (preferredHeight > maxHeight && warningPanel.Visible)
            {
                int availableHeight = Math.Max(1, warningViewport.Height - (preferredHeight - maxHeight));
                SizeContent(availableHeight);
                preferredHeight = layout.PreferredSize.Height + buttons.PreferredSize.Height +
                    buttons.Margin.Vertical + frame.Padding.Vertical;
            }

            int minimumHeight = Math.Min(LogicalToDeviceUnits(MinimumClientHeight), maxHeight);
            ClientSize = new Size(width, Math.Clamp(preferredHeight, minimumHeight, maxHeight));
            frame.PerformLayout();
            contentViewport.PerformLayout();
            if (Visible)
            {
                Location = new Point(
                    Math.Clamp(Left, workingArea.Left, Math.Max(workingArea.Left, workingArea.Right - Width)),
                    Math.Clamp(Top, workingArea.Top, Math.Max(workingArea.Top, workingArea.Bottom - Height)));
            }
        }
        finally
        {
            sizingDialog = false;
        }
    }

    private void SizeContent(int? warningHeightLimit = null)
    {
        api.ItemHeight = Math.Max(LogicalToDeviceUnits(26), api.Font.Height + LogicalToDeviceUnits(8));
        build.ItemHeight = Math.Max(LogicalToDeviceUnits(26), build.Font.Height + LogicalToDeviceUnits(8));

        // Native owner-drawn combo heights can change without invalidating cached table measurements.
        foreach (ComboBox selector in new ComboBox[] { api, build })
        {
            RowStyle row = layout.RowStyles[layout.GetRow(selector)];
            row.SizeType = SizeType.Absolute;
            row.Height = selector.Height + selector.Margin.Vertical;
        }

        int fieldWidth = Math.Max(1, contentViewport.ClientSize.Width - (int)layout.ColumnStyles[0].Width);
        int warningWidth = Math.Max(1, fieldWidth - (int)warningPanel.ColumnStyles[0].Width);
        int lineLimit = (int)Math.Ceiling(warningText.Font.GetHeight(DeviceDpi) * MaximumWarningLines);
        int maxWarningHeight = Math.Min(lineLimit, warningHeightLimit ?? lineLimit);
        warningViewport.AutoScroll = false;
        warningViewport.AutoScrollPosition = Point.Empty;
        warningText.MaximumSize = new Size(warningWidth, 0);
        int textHeight = warningText.GetPreferredSize(new Size(warningWidth, 0)).Height;
        bool scrollWarning = textHeight > maxWarningHeight;
        if (scrollWarning)
        {
            warningText.MaximumSize = new Size(Math.Max(1, warningWidth - SystemInformation.VerticalScrollBarWidth), 0);
            textHeight = warningText.PreferredSize.Height;
        }

        warningViewport.MaximumSize = new Size(0, maxWarningHeight);
        warningViewport.Height = Math.Min(textHeight, maxWarningHeight);
        warningViewport.AutoScroll = scrollWarning;
        detectionHint.MaximumSize = new Size(
            Math.Max(1, fieldWidth - (int)detectionPanel.ColumnStyles[0].Width), 0);
        layout.Width = contentViewport.ClientSize.Width;
        layout.PerformLayout();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        UpdateDialogSize();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        UpdateDialogSize();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        if (replaceConfirmation != null)
        {
            Font? previousFont = confirmationFont;
            confirmationFont = new Font(Font, Font.Style | FontStyle.Bold);
            replaceConfirmation.Font = confirmationFont;
            previousFont?.Dispose();
        }
        UpdateDialogSize();
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        UpdateDialogSize();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (!e.Cancel)
        {
            dialogClosing = true;
            CancelApiDetection();
            detectionIcon.Visible = false;
        }
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Enter && executable.ContainsFocus)
        {
            return true;
        }

        return base.ProcessDialogKey(keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            dialogClosing = true;
            CancelApiDetection();
            confirmationFont?.Dispose();
        }
        base.Dispose(disposing);
    }
}
