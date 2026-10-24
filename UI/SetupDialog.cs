namespace ReShadeCentralized;

internal sealed class SetupDialog : Form
{
    private readonly TextBox root = new()
    {
        Text = StateStore.DefaultDirectory
    };

    public string CentralRoot => root.Text;

    public SetupDialog()
    {
        Text = "Setup \u2014 choose new or existing central storage";
        ClientSize = new(720, 160);
        TableLayoutPanel table = DialogLayout.Table(this);
        DialogLayout.Row(table, "Central folder", DialogLayout.Browse(root, false));
        DialogLayout.Buttons(table, this, "Continue");
    }
}
