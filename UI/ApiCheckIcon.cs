using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace ReShadeCentralized;

internal enum ApiCheckState
{
    Ready,
    Checking,
    Detected,
    Warning
}

internal sealed class ApiCheckIcon : Control
{
    private readonly System.Windows.Forms.Timer animation = new() { Interval = 80 };
    private int angle;
    private ApiCheckState state;

    [DefaultValue(ApiCheckState.Ready)]
    public ApiCheckState State
    {
        get => state;
        set
        {
            state = value;
            angle = 0;
            animation.Enabled = Visible && state == ApiCheckState.Checking;
            AccessibleDescription = state switch
            {
                ApiCheckState.Ready => "Ready to check",
                ApiCheckState.Checking => "Checking graphics API",
                ApiCheckState.Detected => "Graphics API recommendation available",
                _ => "Graphics API check warning"
            };
            Invalidate();
        }
    }

    public ApiCheckIcon()
    {
        Size = new Size(16, 16);
        TabStop = false;
        AccessibleName = "Graphics API check status";
        AccessibleRole = AccessibleRole.Graphic;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        State = ApiCheckState.Ready;
        animation.Tick += (_, _) =>
        {
            angle = (angle + 30) % 360;
            Invalidate();
        };
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        animation.Enabled = Visible && state == ApiCheckState.Checking;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        float scale = Math.Min(ClientSize.Width, ClientSize.Height) / 16f;
        if (scale <= 0)
        {
            return;
        }

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TranslateTransform((Width - (16 * scale)) / 2, (Height - (16 * scale)) / 2);
        e.Graphics.ScaleTransform(scale, scale);
        using var pen = new Pen(ForeColor, 1.5f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var fill = new SolidBrush(ForeColor);
        switch (state)
        {
            case ApiCheckState.Ready:
                e.Graphics.FillEllipse(fill, 6, 6, 4, 4);
                break;
            case ApiCheckState.Checking:
                e.Graphics.DrawArc(pen, 2, 2, 12, 12, angle, 250);
                break;
            case ApiCheckState.Detected:
                e.Graphics.DrawEllipse(pen, 1.5f, 1.5f, 13, 13);
                e.Graphics.FillEllipse(fill, 7.2f, 4, 1.6f, 1.6f);
                e.Graphics.DrawLine(pen, 8, 7, 8, 11.5f);
                break;
            case ApiCheckState.Warning:
                PointF[] triangle = [new(8, 1.5f), new(14.5f, 13.5f), new(1.5f, 13.5f)];
                e.Graphics.DrawPolygon(pen, triangle);
                e.Graphics.DrawLine(pen, 8, 5.5f, 8, 8.5f);
                e.Graphics.FillEllipse(fill, 7.2f, 10.5f, 1.6f, 1.6f);
                break;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            animation.Dispose();
        }
        base.Dispose(disposing);
    }
}
