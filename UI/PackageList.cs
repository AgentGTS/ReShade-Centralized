namespace ReShadeCentralized;

internal sealed class PackageList : CheckedListBox
{
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count)
        {
            return;
        }

        bool selected = (e.State & DrawItemState.Selected) != 0;
        Color background = selected ? DarkTheme.Selection : DarkTheme.Input;
        Color foreground = SystemInformation.HighContrast && selected ? SystemColors.HighlightText : DarkTheme.Text;
        using var fill = new SolidBrush(background);
        e.Graphics.FillRectangle(fill, e.Bounds);

        int size = LogicalToDeviceUnits(14);
        int gap = LogicalToDeviceUnits(8);
        var box = new Rectangle(e.Bounds.Left + gap, e.Bounds.Top + ((e.Bounds.Height - size) / 2), size, size);
        CheckState state = GetItemCheckState(e.Index);
        using var checkFill = new SolidBrush(state == CheckState.Unchecked ? DarkTheme.Raised : DarkTheme.Accent);
        using var border = new Pen(DarkTheme.Border);
        e.Graphics.FillRectangle(checkFill, box);
        e.Graphics.DrawRectangle(border, box);
        using var mark = new Pen(DarkTheme.OnAccent, LogicalToDeviceUnits(2));
        if (state == CheckState.Checked)
        {
            e.Graphics.DrawLines(mark,
            [
                new Point(box.Left + (size / 5), box.Top + (size / 2)),
                new Point(box.Left + (size * 2 / 5), box.Top + (size * 3 / 4)),
                new Point(box.Left + (size * 4 / 5), box.Top + (size / 4))
            ]);
        }
        else if (state == CheckState.Indeterminate)
        {
            e.Graphics.DrawLine(mark, box.Left + 3, box.Top + (size / 2), box.Right - 3, box.Top + (size / 2));
        }

        var textBounds = new Rectangle(box.Right + gap, e.Bounds.Top,
            Math.Max(0, e.Bounds.Right - box.Right - gap), e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), e.Font, textBounds, foreground,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if ((e.State & DrawItemState.Focus) != 0)
        {
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(e.Bounds, -1, -1), foreground, background);
        }
    }
}
