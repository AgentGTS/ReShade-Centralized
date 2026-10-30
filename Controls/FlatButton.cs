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
        MouseEnter += (_, _) => SetInteraction(hovered: true, pressed);
        MouseLeave += (_, _) => SetInteraction(hovered: false, pressed: false);
        MouseDown += (_, e) => SetInteraction(hovered, pressed: e.Button == MouseButtons.Left);
        MouseUp += (_, _) => SetInteraction(hovered, pressed: false);
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Space)
            {
                SetInteraction(hovered, pressed: true);
            }
        };
        KeyUp += (_, _) => SetInteraction(hovered, pressed: false);
        GotFocus += (_, _) => Invalidate();
        LostFocus += (_, _) => SetInteraction(hovered, pressed: false);
        EnabledChanged += (_, _) => SetInteraction(hovered, pressed: false);
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

    private void SetInteraction(bool hovered, bool pressed)
    {
        this.hovered = hovered;
        this.pressed = pressed;
        Invalidate();
    }
}
