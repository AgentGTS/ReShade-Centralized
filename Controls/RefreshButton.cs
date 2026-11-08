using System.Drawing.Drawing2D;

namespace ReShadeCentralized;

internal sealed class RefreshButton : FlatButton
{
    public RefreshButton() : base("")
    {
        AutoSize = false;
        Size = new Size(36, 36);
        Padding = Padding.Empty;
        Margin = new Padding(8, 0, 0, 0);
        Dock = DockStyle.Fill;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        GraphicsState state = e.Graphics.Save();
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = DeviceDpi / 96f;
        e.Graphics.TranslateTransform(Width / 2f, Height / 2f);
        e.Graphics.ScaleTransform(scale, scale);
        using var pen = new Pen(Enabled ? ForeColor : DarkTheme.Muted, 1.7f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        e.Graphics.DrawArc(pen, -7, -7, 14, 14, 35, 290);
        e.Graphics.DrawLines(pen, [new PointF(1, -5), new PointF(6, -4), new PointF(6, -9)]);
        e.Graphics.Restore(state);
    }
}
