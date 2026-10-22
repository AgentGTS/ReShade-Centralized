namespace ReShadeCentralized;

internal sealed class HelpDialog : Form
{
    public HelpDialog()
    {
        Text = "Help";
        ClientSize = new Size(780, 600);
        MinimumSize = new Size(600, 460);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Font = DarkTheme.BodyFont;
        AutoScaleMode = AutoScaleMode.Dpi;

        using Stream stream = typeof(HelpDialog).Assembly.GetManifestResourceStream("ReShadeCentralized.Help.txt")
            ?? throw new InvalidOperationException("The embedded user guide is missing. Reinstall the application.");
        using var reader = new StreamReader(stream);
        var instructions = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            AccessibleName = "User guide",
            Text = reader.ReadToEnd().ReplaceLineEndings(Environment.NewLine)
        };
        instructions.Select(0, 0);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(20)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(instructions, 0, 0);
        var closeButton = new FlatButton("Close")
        {
            DialogResult = DialogResult.OK,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0, 16, 0, 0)
        };
        layout.Controls.Add(closeButton, 0, 1);
        Controls.Add(layout);
        AcceptButton = closeButton;
        CancelButton = closeButton;
        DarkTheme.Apply(this);
    }
}
