using System.Text.Json;

namespace ReShadeCentralized;

internal sealed class GameDialog : Form
{
    private readonly TextBox name = new();
    private readonly TextBox executable = new();
    private readonly DarkComboBox api = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList
    };

    private readonly DarkComboBox build = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList
    };

    private readonly Label note = new()
    {
        AutoSize = true
    };

    private readonly RuntimeBuild global;
    private readonly GameProfile original;

    public GameProfile Result()
    {
        GameProfile game = JsonSerializer.Deserialize<GameProfile>(
            JsonSerializer.Serialize(original, ManagerState.Json),
            ManagerState.Json)!;
        game.Name = name.Text.Trim();
        game.Executable = executable.Text.Trim();
        game.Api = (RenderApi)api.SelectedItem!;
        game.Build = game.Api == RenderApi.Vulkan ? global : (RuntimeBuild)build.SelectedItem!;
        return game;
    }

    public GameDialog(RuntimeBuild global, GameProfile? game = null)
    {
        this.global = global;
        original = game ?? new();
        Text = game == null ? "Install ReShade to a game" : "Repair / edit game";
        ClientSize = new(760, 420);
        TableLayoutPanel table = DialogLayout.Table(this);
        name.Text = original.Name;
        executable.Text = original.Executable ?? "";
        api.Items.AddRange(Enum.GetValues<RenderApi>().Cast<object>().ToArray());
        api.SelectedItem = original.Api;
        build.Items.AddRange(Enum.GetValues<RuntimeBuild>().Cast<object>().ToArray());
        build.SelectedItem = original.Build;
        api.SelectedIndexChanged += (_, _) => UpdateBuild();
        executable.TextChanged += (_, _) =>
        {
            if (name.Text.Length == 0)
            {
                name.Text = Path.GetFileNameWithoutExtension(executable.Text);
            }
        };
        DialogLayout.Row(table, "Game name", name);
        DialogLayout.Row(table, "Runtime executable", DialogLayout.Browse(executable, true));
        DialogLayout.Row(table, "Rendering API", api);
        DialogLayout.Row(table, "ReShade build", build);
        DialogLayout.Row(table, "", note);
        DialogLayout.Buttons(table, this, "Install / repair");
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

        note.Text = vulkan ?
            "Vulkan uses the global build. Your game keeps its own settings and preset." :
            "Select the actual runtime executable. FullAddon supports manually installed add-ons.";
    }
}
