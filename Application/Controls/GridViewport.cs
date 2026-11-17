namespace ReShadeCentralized;

internal abstract class AppGrid : DataGridView {
    protected override void OnVisibleChanged(EventArgs e) {
        base.OnVisibleChanged(e);
        // WinForms can leave scrollbars disabled when a hidden grid is re-enabled.
        if (Visible && Enabled && (!VerticalScrollBar.Enabled || !HorizontalScrollBar.Enabled))
            PerformLayout();
    }
}

internal readonly record struct GridViewport(
    string? TopId, int TopIndex, int HorizontalOffset, string? CurrentId, int Column, string? SelectedId) {
    public static GridViewport Capture(DataGridView grid, Func<DataGridViewRow, string?> id) {
        int top = grid.FirstDisplayedScrollingRowIndex;
        DataGridViewCell? current = grid.CurrentCell;
        return new(top >= 0 ? id(grid.Rows[top]) : null, top, grid.HorizontalScrollingOffset,
            current?.OwningRow is { } row ? id(row) : null, current?.ColumnIndex ?? -1,
            grid.SelectedRows.Count > 0 ? id(grid.SelectedRows[0]) : null);
    }

    public void Restore(DataGridView grid, Func<DataGridViewRow, string?> id, bool preserveIndex = true) {
        if (grid.Rows.Count == 0)
            return;

        int top = -1;
        int current = -1;
        int selected = -1;
        foreach (DataGridViewRow row in grid.Rows) {
            if (id(row) is not { } key)
                continue;
            if (key.Equals(TopId, StringComparison.OrdinalIgnoreCase))
                top = row.Index;
            if (key.Equals(CurrentId, StringComparison.OrdinalIgnoreCase))
                current = row.Index;
            if (key.Equals(SelectedId, StringComparison.OrdinalIgnoreCase))
                selected = row.Index;
        }

        grid.CurrentCell = current >= 0 && Column >= 0 && Column < grid.Columns.Count && grid.Columns[Column].Visible
            ? grid.Rows[current].Cells[Column]
            : null;
        grid.ClearSelection();
        if (selected >= 0)
            grid.Rows[selected].Selected = true;

        if (top < 0 && preserveIndex)
            top = Math.Min(TopIndex, grid.Rows.Count - 1);
        if (top >= 0 && grid.FirstDisplayedScrollingRowIndex != top)
            grid.FirstDisplayedScrollingRowIndex = top;

        if (grid.HorizontalScrollingOffset != HorizontalOffset)
            grid.HorizontalScrollingOffset = HorizontalOffset;
    }
}
