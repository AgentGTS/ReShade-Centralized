using System.Drawing.Drawing2D;

namespace ReShadeCentralized;

internal sealed partial class GameDialog
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


    public GameDialog(ManagerState state, GameProfile? game = null)
    {
        global = state.VulkanBuild;
        original = game ?? new();
        this.state = state;
        managedLinks = state.Games.SelectMany(profile => profile.Links).Concat(original.Links)
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

}
