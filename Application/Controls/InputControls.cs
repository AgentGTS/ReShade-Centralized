namespace ReShadeCentralized;

internal sealed class DarkComboBox : ComboBox {
    public DarkComboBox() {
        DrawMode = DrawMode.OwnerDrawFixed;
        FlatStyle = FlatStyle.Flat;
        IntegralHeight = false;
        ItemHeight = 26;
        DropDownHeight = 240;
        FormattingEnabled = true;
        Format += (_, e) => e.Value = e.ListItem switch {
            RuntimeBuild build => DisplayNames.For(build),
            RenderApi api => DisplayNames.For(api),
            _ => e.Value
        };
    }

    protected override void OnDrawItem(DrawItemEventArgs e) {
        bool selected = (e.State & DrawItemState.Selected) != 0;
        bool closed = (e.State & DrawItemState.ComboBoxEdit) != 0;
        Color background = selected && !closed ? DarkTheme.Selection : DarkTheme.Input;
        Color foreground = Enabled ? DarkTheme.Text : DarkTheme.Muted;
        if (SystemInformation.HighContrast && selected && !closed)
            foreground = SystemColors.HighlightText;

        using var fill = new SolidBrush(background);
        e.Graphics.FillRectangle(fill, e.Bounds);
        string? text = e.Index >= 0 ? GetItemText(Items[e.Index]) : Text;
        Rectangle textBounds = Rectangle.Inflate(e.Bounds, -6, 0);
        TextRenderer.DrawText(
            e.Graphics,
            text,
            e.Font,
            textBounds,
            foreground,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        if ((e.State & DrawItemState.Focus) != 0)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(e.Bounds, -1, -1), foreground, background);
    }
}

internal sealed class ThemedCheckBox : CheckBox {
    public ThemedCheckBox() {
        FlatStyle = FlatStyle.Flat;
        UseVisualStyleBackColor = false;
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Opaque,
            true);
    }

    public override Size GetPreferredSize(Size proposedSize) {
        Size text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine);
        return new Size(
            text.Width + LogicalToDeviceUnits(26) + Padding.Horizontal,
            Math.Max(text.Height, LogicalToDeviceUnits(16)) + Padding.Vertical);
    }

    protected override void OnPaint(PaintEventArgs e) {
        using var background = new SolidBrush(BackColor);
        e.Graphics.FillRectangle(background, ClientRectangle);
        int size = LogicalToDeviceUnits(16);
        int gap = LogicalToDeviceUnits(10);
        bool checkOnRight = CheckAlign == ContentAlignment.MiddleRight;
        var box = new Rectangle(
            checkOnRight ? ClientSize.Width - Padding.Right - size : Padding.Left,
            Padding.Top + ((ClientSize.Height - Padding.Vertical - size) / 2),
            size,
            size);
        DarkTheme.DrawCheckBox(e.Graphics, box, Checked, !Enabled, LogicalToDeviceUnits(2));

        var textBounds = new Rectangle(
            checkOnRight ? Padding.Left : box.Right + gap,
            Padding.Top,
            Math.Max(0, ClientSize.Width - Padding.Horizontal - size - gap),
            ClientSize.Height - Padding.Vertical);
        TextRenderer.DrawText(
            e.Graphics,
            Text,
            Font,
            textBounds,
            Enabled ? ForeColor : DarkTheme.Muted,
            TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(e.Graphics, textBounds, ForeColor, BackColor);
    }
}
