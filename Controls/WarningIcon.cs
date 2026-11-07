using System.Drawing.Drawing2D;

namespace ReShadeCentralized;

internal static class WarningIcon
{
    public static void Draw(Graphics graphics, Rectangle bounds, Color color)
    {
        float scale = Math.Min(bounds.Width, bounds.Height) / 16f;
        if (scale <= 0)
        {
            return;
        }

        GraphicsState saved = graphics.Save();
        try
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TranslateTransform(
                bounds.Left + ((bounds.Width - (16 * scale)) / 2),
                bounds.Top + ((bounds.Height - (16 * scale)) / 2));
            graphics.ScaleTransform(scale, scale);
            using var pen = new Pen(color, 1.5f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            using var fill = new SolidBrush(color);

            PointF[] triangle = [new(8, 1.5f), new(14.5f, 13.5f), new(1.5f, 13.5f)];
            graphics.DrawPolygon(pen, triangle);
            graphics.DrawLine(pen, 8, 5.5f, 8, 8.5f);
            graphics.FillEllipse(fill, 7.2f, 10.5f, 1.6f, 1.6f);
        }
        finally
        {
            graphics.Restore(saved);
        }
    }
}
