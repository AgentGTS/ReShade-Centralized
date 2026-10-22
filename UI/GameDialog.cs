using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace ReShadeCentralized;

internal sealed class GameDialog : Form
{
    private const string ApiPlaceholder = "-- Select graphics API --";
    private const string BuildPlaceholder = "-- Select ReShade build --";
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
    private readonly Label buildLabel;
    private readonly Button installButton;
    private readonly IReadOnlyList<OwnedLink> managedLinks;
    private readonly TableLayoutPanel warningPanel = new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Dock = DockStyle.Fill,
        ColumnCount = 2,
        Margin = Padding.Empty,
        Visible = false
    };

    private readonly Label warningText = new()
    {
        AutoSize = true,
        Dock = DockStyle.Fill,
        Margin = Padding.Empty,
        AccessibleName = "Existing ReShade installation"
    };

    private readonly RuntimeBuild global;
    private readonly GameProfile original;
    private readonly Func<string, CancellationToken, Task<RenderApi?>> apiDetector;
    private CancellationTokenSource? detectionCancellation;
    private int executableVersion;
    private RuntimeBuild? nonVulkanBuild;
    private bool usingGlobalBuild;
    private bool dialogClosing;

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
        Func<string, CancellationToken, Task<RenderApi?>>? apiDetector = null)
    {
        this.global = global;
        this.apiDetector = apiDetector ?? ((path, cancellationToken) =>
            Task.Run(() => GraphicsApiDetector.Detect(path, cancellationToken), cancellationToken));
        original = game ?? new();
        managedLinks = (managedGames ?? []).SelectMany(profile => profile.Links).Concat(original.Links)
            .DistinctBy(link => link.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        Text = game == null ? "Install game" : "Repair / edit game";
        ClientSize = new(760, 246);
        layout = DialogLayout.Table(this);
        layout.Dock = DockStyle.Top;
        layout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        executable.Text = original.Executable ?? "";
        api.Items.Add(ApiPlaceholder);
        api.Items.AddRange(Enum.GetValues<RenderApi>().Cast<object>().ToArray());
        api.SelectedItem = game?.Executable != null ? original.Api : ApiPlaceholder;
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
            AccessibleName = "Warning"
        };
        warningIcon.Paint += (_, e) => e.Graphics.DrawIcon(SystemIcons.Warning, warningIcon.ClientRectangle);
        warningPanel.Controls.Add(warningIcon, 0, 0);
        warningPanel.Controls.Add(warningText, 1, 0);
        DialogLayout.Row(layout, "", warningPanel, fullWidth: false, margin: new Padding(0, 8, 0, 0));
        DialogLayout.Row(layout, "Graphics API", api, margin: new Padding(0, 16, 0, 0));
        buildLabel = DialogLayout.Row(layout, "Build", build, margin: new Padding(0, 16, 0, 0))!;
        installButton = DialogLayout.Buttons(layout, this, game == null ? "Install" : "Repair");
        Control buttons = installButton.Parent!;
        buttons.Margin = new Padding(0, 24, 0, 0);
        foreach (Control button in buttons.Controls)
        {
            button.Margin = button == installButton ? Padding.Empty : new Padding(0, 0, 8, 0);
        }
        detectionHint.LinkClicked += async (_, _) => await DetectApiAsync();
        detectionHint.MouseEnter += (_, _) => HighlightCheckLink(true);
        detectionHint.MouseLeave += (_, _) => HighlightCheckLink(false);
        api.SelectedIndexChanged += (_, _) => UpdateBuild();
        build.SelectedIndexChanged += (_, _) => UpdateInstallationNotice();
        executable.TextChanged += (_, _) =>
        {
            CancelApiDetection();
            SetDetectionHint("Check graphics API", ApiCheckState.Ready);
            UpdateInstallationNotice();
        };
        if (game?.Executable != null && original.Api == RenderApi.Vulkan)
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
        UpdateDialogHeight();
    }

    private void UpdateInstallationNotice()
    {
        ExistingInstallationNotice? notice;
        try
        {
            notice = DeploymentService.InspectExistingInstallation(
                executable.Text.Trim(), api.SelectedItem as RenderApi?, managedLinks,
                original.Directory.Length == 0 ? null : ConfigurationService.ConfigPath(original));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            ArgumentException or Win32Exception)
        {
            notice = new("Cannot inspect the executable folder. Check the path and folder permissions, " +
                "then select the executable again. " + error.Message, true);
        }

        string path = executable.Text.Trim();
        installButton.Enabled = PortableExecutable.GetValidatedGamePath(path) != null &&
            api.SelectedItem is RenderApi && build.SelectedItem is RuntimeBuild &&
            notice?.BlocksInstallation != true;
        warningText.Text = (notice?.Message ?? "").ReplaceLineEndings(Environment.NewLine);
        warningText.ForeColor = DarkTheme.Warning;
        warningPanel.Visible = notice != null;
        UpdateDialogHeight();
    }

    private void UpdateDialogHeight()
    {
        int iconWidth = LogicalToDeviceUnits(24);
        int fieldWidth = Math.Max(1, layout.ClientSize.Width - layout.Padding.Horizontal -
            (int)layout.ColumnStyles[0].Width);
        int availableWidth = Math.Max(1, fieldWidth - iconWidth);
        warningText.MaximumSize = new Size(availableWidth, 0);
        detectionHint.MaximumSize = new Size(Math.Max(1, fieldWidth - iconWidth), 0);
        layout.PerformLayout();
        ClientSize = new Size(ClientSize.Width, layout.GetPreferredSize(new Size(ClientSize.Width, 0)).Height);
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
        }
        base.Dispose(disposing);
    }
}
