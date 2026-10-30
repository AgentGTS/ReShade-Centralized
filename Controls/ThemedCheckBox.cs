namespace ReShadeCentralized;

internal sealed class ThemedCheckBox : CheckBox
{
    public ThemedCheckBox()
    {
        FlatStyle = FlatStyle.Flat;
        UseVisualStyleBackColor = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Opaque, true);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        Size text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine);
        return new Size(text.Width + LogicalToDeviceUnits(26) + Padding.Horizontal,
            Math.Max(text.Height, LogicalToDeviceUnits(16)) + Padding.Vertical);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var background = new SolidBrush(BackColor);
        e.Graphics.FillRectangle(background, ClientRectangle);
        int size = LogicalToDeviceUnits(16);
        int gap = LogicalToDeviceUnits(10);
        bool checkOnRight = CheckAlign == ContentAlignment.MiddleRight;
        var box = new Rectangle(checkOnRight ? ClientSize.Width - Padding.Right - size : Padding.Left,
            Padding.Top + ((ClientSize.Height - Padding.Vertical - size) / 2),
            size, size);
        DarkTheme.DrawCheckBox(e.Graphics, box, Checked, !Enabled, LogicalToDeviceUnits(2));
        var textBounds = new Rectangle(checkOnRight ? Padding.Left : box.Right + gap, Padding.Top,
            Math.Max(0, ClientSize.Width - Padding.Horizontal - size - gap),
            ClientSize.Height - Padding.Vertical);
        TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, Enabled ? ForeColor : DarkTheme.Muted,
            TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        if (Focused && ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(e.Graphics, textBounds, ForeColor, BackColor);
        }
    }
}
