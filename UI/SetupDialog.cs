namespace ReShadeCentralized;

internal sealed class SetupDialog : Form
{
    private readonly TextBox root = new()
    {
        Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ReShade Centralized")
    };

    private readonly TextBox legacy = new();
    public string CentralRoot => root.Text;
    public string? LegacyIni => string.IsNullOrWhiteSpace(legacy.Text) ? null : legacy.Text;

    public SetupDialog(string applicationDirectory)
    {
        Text = "Set up ReShade Centralized";
        ClientSize = new(720, 330);
        string old = Path.Combine(applicationDirectory, "ReShadeCentralized.ini");
        if (File.Exists(old))
        {
            legacy.Text = old;
        }

        TableLayoutPanel table = DialogLayout.Table(this);
        DialogLayout.Row(table, "Central folder", DialogLayout.Browse(root, false));
        DialogLayout.Row(table, "Legacy settings", DialogLayout.Browse(legacy, true, "Legacy settings|*.ini"));
        DialogLayout.Row(
            table,
            "",
            new Label
            {
                Text = "Legacy import preserves your existing paths and profiles.\n" +
                    "Leave it blank for a new installation.",
                AutoSize = true
            });
        DialogLayout.Buttons(table, this, "Create / import");
    }
}
