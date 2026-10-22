namespace ReShadeCentralized;

internal sealed class FlatButton : Button
{
    private readonly bool primary;
    private bool hovered;
    private bool pressed;

    public FlatButton(string text, bool primary = false)
    {
        this.primary = primary;
        Text = text;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(0, 36);
        Padding = new Padding(12, 6, 12, 6);
        Margin = new Padding(0, 0, 8, 8);
        FlatStyle = FlatStyle.Flat;
        UseVisualStyleBackColor = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer, true);
        ApplyTheme();
    }

    public void ApplyTheme()
    {
        BackColor = primary ? DarkTheme.Accent : DarkTheme.Raised;
        ForeColor = primary ? DarkTheme.OnAccent : DarkTheme.Text;
        FlatAppearance.BorderSize = primary ? 0 : 1;
        FlatAppearance.BorderColor = DarkTheme.Border;
        FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(138, 235, 211) : DarkTheme.Hover;
        FlatAppearance.MouseDownBackColor = primary ? DarkTheme.Accent : DarkTheme.Selection;
        if (SystemInformation.HighContrast)
        {
            FlatAppearance.MouseOverBackColor = BackColor;
            FlatAppearance.MouseDownBackColor = BackColor;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Color background = Enabled ? BackColor : DarkTheme.Raised;
        if (Enabled && pressed)
        {
            background = FlatAppearance.MouseDownBackColor;
        }
        else if (Enabled && hovered)
        {
            background = FlatAppearance.MouseOverBackColor;
        }

        Color disabledForeground = SystemInformation.HighContrast ? SystemColors.GrayText : DarkTheme.Muted;
        Color foreground = Enabled ? ForeColor : disabledForeground;
        using var fill = new SolidBrush(background);
        e.Graphics.FillRectangle(fill, ClientRectangle);
        if (FlatAppearance.BorderSize > 0 || (Focused && ShowFocusCues))
        {
            using var border = new Pen(Focused && ShowFocusCues ? DarkTheme.Accent : DarkTheme.Border);
            e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }
        TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
            | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        if (!ShowKeyboardCues)
        {
            flags |= TextFormatFlags.HidePrefix;
        }

        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, foreground, flags);
        if (Focused && ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4),
                foreground, background);
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hovered = false;
        pressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        pressed = e.Button == MouseButtons.Left;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        pressed = false;
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Space)
        {
            pressed = true;
            Invalidate();
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        pressed = false;
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        pressed = false;
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        pressed = false;
        Invalidate();
    }
}
