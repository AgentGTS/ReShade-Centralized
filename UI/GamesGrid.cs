namespace ReShadeCentralized;

internal sealed class GamesGrid : DataGridView
{
    public GamesGrid()
    {
        DoubleBuffered = true;
        AutoGenerateColumns = false;
        AddColumn("Id", "Id", 1).Visible = false;
        AddColumn("Name", "Game", 24);
        AddColumn("API", "API", 12);
        AddColumn("Build", "Build", 12);
        AddColumn("Architecture", "Arch.", 10);
        AddColumn("Executable", "Executable", 42);
    }

    private DataGridViewColumn AddColumn(string name, string heading, int weight)
    {
        var column = new DataGridViewTextBoxColumn
        {
            Name = name,
            DataPropertyName = name,
            HeaderText = heading,
            FillWeight = weight,
            MinimumWidth = 60
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
        var titleBounds = new Rectangle(16, center - 36, Width - 32, 32);
        var detailBounds = new Rectangle(16, center + 2, Width - 32, 48);
        TextRenderer.DrawText(e.Graphics, "No managed games yet", DarkTheme.HeadingFont, titleBounds,
            DarkTheme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(e.Graphics, "Use Install to game to add an executable.", Font, detailBounds,
            DarkTheme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
    }
}
