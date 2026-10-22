namespace ReShadeCentralized;

internal sealed class GamesGrid : DataGridView
{
    public GamesGrid()
    {
        DoubleBuffered = true;
        AutoGenerateColumns = false;
        AddColumn("Id", "Id").Visible = false;
        AddColumn("Name", "Game");
        AddColumn("API", "API");
        AddColumn("Build", "Build");
        AddColumn("Architecture", "Arch.");
        AddColumn("Executable", "Executable");
    }

    private DataGridViewColumn AddColumn(string name, string heading)
    {
        var column = new DataGridViewTextBoxColumn
        {
            Name = name,
            DataPropertyName = name,
            HeaderText = heading
        };
        Columns.Add(column);
        return column;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (RowCount != 0)
        {
            return;
        }

        int center = ColumnHeadersHeight + ((Height - ColumnHeadersHeight) / 2);
        var titleBounds = new Rectangle(16, center - 16, Width - 32, 32);
        TextRenderer.DrawText(e.Graphics, "No games", DarkTheme.HeadingFont, titleBounds,
            DarkTheme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}
