namespace ReShadeCentralized;

internal sealed class GamesGrid : DataGridView
{
    public GamesGrid()
    {
        DoubleBuffered = true;
        AutoGenerateColumns = false;
        AddColumn("Id", "Id").Visible = false;
        AddWarningColumn("InstallationWarning", "Installation status");
        AddWarningColumn("ShaderWarning", "Preset shader status");
        ShowCellToolTips = true;
        DataGridViewColumn name = AddColumn("Name", "Game");
        name.DefaultCellStyle.Padding = new Padding(4, 2, 8, 2);
        name.HeaderCell.Style.Padding = new Padding(4, 0, 8, 0);
        AddColumn("API", "API");
        AddColumn("Build", "Build");
        AddColumn("Architecture", "Arch.");
        AddColumn("Executable", "Executable");
    }

    private void AddWarningColumn(string name, string description)
    {
        DataGridViewColumn status = AddColumn(name, "");
        status.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
        status.Width = LogicalToDeviceUnits(24);
        status.Resizable = DataGridViewTriState.False;
        status.SortMode = DataGridViewColumnSortMode.NotSortable;
        status.DefaultCellStyle.Padding = Padding.Empty;
        status.ToolTipText = description;
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

    protected override void OnCellPainting(DataGridViewCellPaintingEventArgs e)
    {
        base.OnCellPainting(e);
        if (e.RowIndex < 0 || e.ColumnIndex < 0
            || Columns[e.ColumnIndex].Name is not ("InstallationWarning" or "ShaderWarning"))
        {
            return;
        }

        e.Paint(e.ClipBounds, e.PaintParts & ~DataGridViewPaintParts.ContentForeground);
        if (e.Value is string { Length: > 0 })
        {
            int size = LogicalToDeviceUnits(16);
            var bounds = new Rectangle(e.CellBounds.Left + ((e.CellBounds.Width - size) / 2),
                e.CellBounds.Top + ((e.CellBounds.Height - size) / 2), size, size);
            if (e.Graphics != null)
            {
                Color color = Columns[e.ColumnIndex].Name == "InstallationWarning"
                    ? DarkTheme.Danger
                    : DarkTheme.Warning;
                if (SystemInformation.HighContrast)
                {
                    color = (e.State & DataGridViewElementStates.Selected) != 0
                        ? SystemColors.HighlightText : SystemColors.WindowText;
                }
                WarningIcon.Draw(e.Graphics, bounds, color);
            }
        }

        e.Handled = true;
    }

    protected override void OnDataBindingComplete(DataGridViewBindingCompleteEventArgs e)
    {
        base.OnDataBindingComplete(e);
        foreach (DataGridViewRow row in Rows)
        {
            foreach (string name in new[] { "InstallationWarning", "ShaderWarning" })
            {
                DataGridViewCell cell = row.Cells[name];
                cell.ToolTipText = cell.Value as string ?? "";
            }
        }
    }

    protected override void OnCellValueChanged(DataGridViewCellEventArgs e)
    {
        base.OnCellValueChanged(e);
        if (e.RowIndex >= 0 && e.ColumnIndex >= 0
            && Columns[e.ColumnIndex].Name is "InstallationWarning" or "ShaderWarning")
        {
            DataGridViewCell cell = Rows[e.RowIndex].Cells[e.ColumnIndex];
            cell.ToolTipText = cell.Value as string ?? "";
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (RowCount != 0 || Controls.OfType<StorageLoadingIndicator>().Any(control => control.Visible))
        {
            return;
        }

        int center = ColumnHeadersHeight + ((Height - ColumnHeadersHeight) / 2);
        var titleBounds = new Rectangle(16, center - 16, Width - 32, 32);
        TextRenderer.DrawText(e.Graphics, "No games", DarkTheme.HeadingFont, titleBounds,
            DarkTheme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}
