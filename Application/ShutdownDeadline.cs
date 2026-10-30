namespace ReShadeCentralized;

internal sealed class ShutdownDeadline(int exitCode = 2) : IDisposable
{
    private readonly object gate = new();
    private System.Threading.Timer? timer;
    private bool requested;
    private bool disposed;
    private bool forced;

    internal void Request()
    {
        bool force;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            force = requested;
            if (!requested)
            {
                requested = true;
                // A UI timer could never fire if the window thread stalls during shutdown.
                timer = new System.Threading.Timer(_ => ForceExit(), null, TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
            }
        }
        if (force)
        {
            ForceExit();
        }
    }

    private void ForceExit()
    {
        lock (gate)
        {
            if (disposed || forced)
            {
                return;
            }
            forced = true;
            ProcessTermination.Exit(exitCode);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            timer?.Dispose();
            timer = null;
        }
    }
}
