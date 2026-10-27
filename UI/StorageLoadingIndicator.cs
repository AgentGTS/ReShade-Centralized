using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace ReShadeCentralized;

internal sealed record StorageLoadProgress(string Phase, int Completed = 0, int Total = 0);

internal sealed class StorageLoadingIndicator : Control
{
    private readonly System.Windows.Forms.Timer animation = new() { Interval = 40 };
    private readonly Stopwatch elapsed = new();
    private StorageLoadProgress progress = new("Opening saved state");
    private bool busy;

    internal bool IsAnimating => animation.Enabled;
    internal bool IsBusy => busy;

    public StorageLoadingIndicator()
    {
        Visible = false;
        TabStop = false;
        AccessibleRole = AccessibleRole.ProgressBar;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        animation.Tick += (_, _) => Invalidate();
    }

    internal void BeginLoading()
    {
        busy = true;
        Text = "Loading central storage";
        AccessibleName = Text;
        progress = new("Opening saved state");
        elapsed.Restart();
        Visible = true;
        UpdateAnimation();
        Invalidate();
    }

    internal void Report(StorageLoadProgress value)
    {
        if (!busy)
        {
            return;
        }
        progress = value;
        AccessibleDescription = PhaseText();
        Invalidate();
    }

    internal void EndLoading(string? failure = null)
    {
        busy = false;
        elapsed.Stop();
        animation.Stop();
        Visible = failure != null;
        Text = failure ?? "";
        AccessibleName = Text;
        AccessibleDescription = failure == null ? "" : "Choose Retry to load central storage.";
        progress = new(AccessibleDescription);
        Invalidate();
    }

    private string PhaseText() => progress.Total > 0 ?
        $"{progress.Phase} ({progress.Completed:N0} / {progress.Total:N0})" : progress.Phase;

    private void UpdateAnimation() =>
        animation.Enabled = busy && Visible && SystemInformation.UIEffectsEnabled;

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdateAnimation();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(DarkTheme.Input);
        float scale = DeviceDpi / 96f;
        float center = Width / 2f;
        float top = (Height / 2f) - (70 * scale);
        if (busy)
        {
            var ring = new RectangleF(center - (20 * scale), top, 40 * scale, 40 * scale);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var track = new Pen(DarkTheme.Border, 3 * scale);
            using var accent = new Pen(DarkTheme.Accent, 3 * scale)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            e.Graphics.DrawEllipse(track, ring);
            float angle = SystemInformation.UIEffectsEnabled ? (elapsed.ElapsedMilliseconds % 1200) * 0.3f : -90;
            e.Graphics.DrawArc(accent, ring, angle, 250);
        }

        var title = new Rectangle(12, (int)(top + (56 * scale)), Width - 24, (int)(32 * scale));
        TextRenderer.DrawText(e.Graphics, Text, DarkTheme.HeadingFont, title, DarkTheme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        var phase = new Rectangle(12, title.Bottom + (int)(6 * scale), Width - 24, (int)(26 * scale));
        TextRenderer.DrawText(e.Graphics, PhaseText(), Font, phase, DarkTheme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (!busy)
        {
            return;
        }

        int width = Math.Min((int)(300 * scale), Math.Max(1, Width - (int)(64 * scale)));
        var bar = new Rectangle((Width - width) / 2, phase.Bottom + (int)(18 * scale), width, (int)(5 * scale));
        using var background = new SolidBrush(DarkTheme.Border);
        using var fill = new SolidBrush(DarkTheme.Accent);
        e.Graphics.FillRectangle(background, bar);
        if (progress.Total > 0)
        {
            float fraction = Math.Clamp((float)progress.Completed / progress.Total, 0, 1);
            e.Graphics.FillRectangle(fill, bar.X, bar.Y, bar.Width * fraction, bar.Height);
        }
        else
        {
            int segment = Math.Max(1, width / 4);
            double cycle = SystemInformation.UIEffectsEnabled ? (elapsed.ElapsedMilliseconds % 1600) / 1600.0 : 0.4;
            int left = Math.Max(bar.Left, bar.Left - segment + (int)((width + segment) * cycle));
            int right = Math.Min(bar.Right, bar.Left + (int)((width + segment) * cycle));
            e.Graphics.FillRectangle(fill, left, bar.Top, Math.Max(1, right - left), bar.Height);
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
