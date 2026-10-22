namespace ReShadeCentralized;

internal sealed class DarkComboBox : ComboBox
{
    public DarkComboBox()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        FlatStyle = FlatStyle.Flat;
        IntegralHeight = false;
        ItemHeight = 26;
        DropDownHeight = 240;
        FormattingEnabled = true;
        Format += (_, e) => e.Value = e.ListItem switch
        {
            RuntimeBuild build => DisplayNames.For(build),
            RenderApi api => DisplayNames.For(api),
            _ => e.Value
        };
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        bool selected = (e.State & DrawItemState.Selected) != 0;
        bool closed = (e.State & DrawItemState.ComboBoxEdit) != 0;
        Color background = selected && !closed ? DarkTheme.Selection : DarkTheme.Input;
        Color foreground = Enabled ? DarkTheme.Text : DarkTheme.Muted;
        if (SystemInformation.HighContrast && selected && !closed)
        {
            foreground = SystemColors.HighlightText;
        }

        using var fill = new SolidBrush(background);
        e.Graphics.FillRectangle(fill, e.Bounds);
        string? text = e.Index >= 0 ? GetItemText(Items[e.Index]) : Text;
        Rectangle textBounds = Rectangle.Inflate(e.Bounds, -6, 0);
        TextRenderer.DrawText(e.Graphics, text, e.Font, textBounds, foreground,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if ((e.State & DrawItemState.Focus) != 0)
        {
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(e.Bounds, -1, -1), foreground, background);
        }
    }
}
