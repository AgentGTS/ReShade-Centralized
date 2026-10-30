namespace ReShadeCentralized;

internal sealed class NavigationTabs : TabControl
{
    private int hoveredIndex = -1;

    public NavigationTabs()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SizeMode = TabSizeMode.Fixed;
        ItemSize = new Size(160, 44);
        Padding = new Point(12, 6);
        SelectedIndexChanged += (_, _) => Invalidate();
        EnabledChanged += (_, _) => Invalidate();
        GotFocus += (_, _) => Invalidate();
        LostFocus += (_, _) => Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(DarkTheme.Background);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var border = new Pen(DarkTheme.Border);
        Rectangle pageBounds = DisplayRectangle;
        pageBounds.Inflate(1, 1);
        e.Graphics.DrawRectangle(border, pageBounds);

        for (var index = 0; index < TabCount; index++)
        {
            Rectangle bounds = GetTabRect(index);
            bool selected = SelectedIndex == index;
            Color background = selected ? DarkTheme.Surface
                : hoveredIndex == index ? DarkTheme.Raised : DarkTheme.Background;
            using var fill = new SolidBrush(background);
            e.Graphics.FillRectangle(fill, bounds);
            Color foreground = selected ? DarkTheme.Text : DarkTheme.Muted;
            if (!Enabled)
            {
                foreground = SystemColors.GrayText;
            }

            TextRenderer.DrawText(e.Graphics, TabPages[index].Text, Font, bounds, foreground,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (selected)
            {
                using var accent = new SolidBrush(DarkTheme.Accent);
                int thickness = LogicalToDeviceUnits(3);
                e.Graphics.FillRectangle(accent, bounds.Left + 12, bounds.Bottom - thickness,
                    bounds.Width - 24, thickness);
                if (Focused && ShowFocusCues)
                {
                    Rectangle focus = Rectangle.Inflate(bounds, -6, -6);
                    ControlPaint.DrawFocusRectangle(e.Graphics, focus, foreground, background);
                }
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hovered = -1;
        for (var index = 0; index < TabCount; index++)
        {
            if (GetTabRect(index).Contains(e.Location))
            {
                hovered = index;
                break;
            }
        }

        if (hoveredIndex != hovered)
        {
            hoveredIndex = hovered;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hoveredIndex = -1;
        Invalidate();
    }

}
