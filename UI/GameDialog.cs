using System.ComponentModel;
using System.Text.Json;

namespace ReShadeCentralized;

internal sealed class GameDialog : Form
{
    private const int BaseClientHeight = 246;
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

    private readonly Label buildLabel;
    private readonly Button installButton;
    private readonly IReadOnlyList<OwnedLink> managedLinks;
    private readonly TableLayoutPanel warningPanel = new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Dock = DockStyle.Fill,
        ColumnCount = 2,
        Margin = new Padding(0, 8, 0, 0),
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

    public GameProfile Result()
    {
        GameProfile game = JsonSerializer.Deserialize<GameProfile>(
            JsonSerializer.Serialize(original, ManagerState.Json),
            ManagerState.Json)!;
        game.Executable = executable.Text.Trim();
        game.Name = Path.GetFileName(game.Executable);
        game.Api = (RenderApi)api.SelectedItem!;
        game.Build = game.Api == RenderApi.Vulkan ? global : (RuntimeBuild)build.SelectedItem!;
        return game;
    }

    public GameDialog(
        RuntimeBuild global,
        GameProfile? game = null,
        IReadOnlyList<GameProfile>? managedGames = null)
    {
        this.global = global;
        original = game ?? new();
        managedLinks = (managedGames ?? []).SelectMany(profile => profile.Links).Concat(original.Links)
            .DistinctBy(link => link.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        Text = game == null ? "Install game" : "Repair / edit game";
        ClientSize = new(760, BaseClientHeight);
        TableLayoutPanel table = DialogLayout.Table(this);
        executable.Text = original.Executable ?? "";
        api.Items.AddRange(Enum.GetValues<RenderApi>().Cast<object>().ToArray());
        api.SelectedItem = original.Api;
        build.Items.AddRange(Enum.GetValues<RuntimeBuild>().Cast<object>().ToArray());
        build.SelectedItem = original.Build;
        api.SelectedIndexChanged += (_, _) => UpdateBuild();
        executable.TextChanged += (_, _) => UpdateInstallationNotice();

        var executableLayout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty
        };
        executableLayout.ColumnStyles.Add(new(SizeType.Percent, 100));
        Control browse = DialogLayout.Browse(executable, true);
        browse.Dock = DockStyle.Fill;
        executableLayout.Controls.Add(browse);
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
        executableLayout.Controls.Add(warningPanel);
        DialogLayout.Row(table, "Executable", executableLayout);
        DialogLayout.Row(table, "Graphics API", api);
        buildLabel = DialogLayout.Row(table, "Build", build)!;
        installButton = DialogLayout.Buttons(table, this, game == null ? "Install" : "Repair");
        UpdateBuild();
    }

    private void UpdateBuild()
    {
        bool vulkan = (RenderApi)api.SelectedItem! == RenderApi.Vulkan;
        build.Enabled = !vulkan;
        if (vulkan)
        {
            build.SelectedItem = global;
        }

        buildLabel.Text = vulkan ? "Global build" : "Build";
        UpdateInstallationNotice();
    }

    private void UpdateInstallationNotice()
    {
        ExistingInstallationNotice? notice;
        try
        {
            notice = DeploymentService.InspectExistingInstallation(
                executable.Text.Trim(), (RenderApi)api.SelectedItem!, managedLinks,
                original.Directory.Length == 0 ? null : ConfigurationService.ConfigPath(original));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            ArgumentException or Win32Exception)
        {
            notice = new("Cannot inspect the executable folder. Check the path and folder permissions, " +
                "then select the executable again. " + error.Message, true);
        }

        string path = executable.Text.Trim();
        installButton.Enabled = File.Exists(path) &&
            Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
            notice?.BlocksInstallation != true;
        warningPanel.Visible = notice != null;
        warningText.Text = notice?.Message ?? "";
        warningText.ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(242, 199, 91);
        PerformLayout();
        int iconWidth = LogicalToDeviceUnits(24);
        int availableWidth = Math.Max(1, warningPanel.Parent!.ClientSize.Width - iconWidth);
        warningText.MaximumSize = new Size(availableWidth, 0);
        int warningHeight = notice == null ? 0 :
            warningPanel.GetPreferredSize(new Size(availableWidth + iconWidth, 0)).Height + warningPanel.Margin.Vertical;
        ClientSize = new Size(ClientSize.Width, LogicalToDeviceUnits(BaseClientHeight) + warningHeight);
    }
}
