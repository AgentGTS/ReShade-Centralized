using System.Runtime.InteropServices;

namespace ReShadeCentralized;

internal static class DarkTheme
{
    private const int ImmersiveDarkModeAttribute = 20;
    private const int WindowCornerPreferenceAttribute = 33;
    private const int CaptionColorAttribute = 35;
    private const int CaptionTextColorAttribute = 36;
    public static Color Background =>
        SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(19, 22, 27);
    public static Color Surface => SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(27, 32, 39);
    public static Color Raised => SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(35, 42, 51);
    public static Color Input => SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(22, 27, 33);
    public static Color Border => SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(55, 65, 78);
    public static Color Text =>
        SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(232, 237, 243);
    public static Color Muted =>
        SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(162, 175, 191);
    public static Color Accent =>
        SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(104, 221, 192);
    public static Color Warning =>
        SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(242, 199, 91);
    public static Color OnAccent =>
        SystemInformation.HighContrast ? SystemColors.HighlightText : Color.FromArgb(15, 35, 32);
    public static Color Hover => SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(49, 61, 73);
    public static Color Selection =>
        SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(40, 69, 69);

    public static Font BodyFont { get; } = new("Segoe UI", 10);
    public static Font HeadingFont { get; } = new("Segoe UI", 15, FontStyle.Bold);
    public static Font MonospaceFont { get; } = new("Consolas", 10);

    public static void Apply(Form form)
    {
        form.Font = BodyFont;
        form.BackColor = Background;
        form.ForeColor = Text;
        ApplyControls(form);
        form.HandleCreated += (_, _) => ApplyWindowChrome(form);
        if (form.IsHandleCreated)
        {
            ApplyWindowChrome(form);
        }
    }

    private static void ApplyControls(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            switch (control)
            {
                case TabPage page:
                    page.BackColor = Surface;
                    page.ForeColor = Text;
                    break;
                case FlatButton button:
                    button.ApplyTheme();
                    break;
                case TextBox textBox:
                    textBox.BackColor = Input;
                    textBox.ForeColor = Text;
                    textBox.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case ComboBox comboBox:
                    comboBox.BackColor = Input;
                    comboBox.ForeColor = Text;
                    comboBox.FlatStyle = FlatStyle.Flat;
                    break;
                case ListBox listBox:
                    listBox.BackColor = Input;
                    listBox.ForeColor = Text;
                    listBox.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case DataGridView grid:
                    ApplyGrid(grid);
                    break;
                case Label label:
                    label.UseMnemonic = false;
                    label.ForeColor = label.Font.Bold ? Text : Muted;
                    break;
            }

            ApplyControls(control);
        }
    }

    private static void ApplyGrid(DataGridView grid)
    {
        grid.BackgroundColor = Input;
        grid.BorderStyle = BorderStyle.None;
        grid.GridColor = Border;
        grid.EnableHeadersVisualStyles = false;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.ColumnHeadersHeight = 42;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Raised,
            ForeColor = Muted,
            SelectionBackColor = Raised,
            SelectionForeColor = Text,
            Font = BodyFont,
            Padding = new Padding(12, 0, 8, 0)
        };
        grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Input,
            ForeColor = Text,
            SelectionBackColor = Selection,
            SelectionForeColor = SystemInformation.HighContrast ? SystemColors.HighlightText : Text,
            Padding = new Padding(12, 6, 8, 6)
        };
        grid.AlternatingRowsDefaultCellStyle.BackColor = Surface;
        grid.RowTemplate.Height = 42;
        grid.AllowUserToResizeRows = false;
    }

    private static void ApplyWindowChrome(Form form)
    {
        int darkMode = SystemInformation.HighContrast ? 0 : 1;
        int squareCorners = 1;
        // Unsupported DWM attributes are ignored by older Windows versions.
        DwmSetWindowAttribute(form.Handle, ImmersiveDarkModeAttribute, ref darkMode, sizeof(int));
        DwmSetWindowAttribute(form.Handle, WindowCornerPreferenceAttribute, ref squareCorners, sizeof(int));
        if (!SystemInformation.HighContrast)
        {
            int captionColor = Background.R | (Background.G << 8) | (Background.B << 16);
            int captionTextColor = Text.R | (Text.G << 8) | (Text.B << 16);
            DwmSetWindowAttribute(form.Handle, CaptionColorAttribute, ref captionColor, sizeof(int));
            DwmSetWindowAttribute(form.Handle, CaptionTextColorAttribute, ref captionTextColor, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
