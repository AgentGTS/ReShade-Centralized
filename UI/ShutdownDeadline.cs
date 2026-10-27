namespace ReShadeCentralized;

internal sealed class ShutdownDeadline : IDisposable
{
    private readonly Action<int> exit;
    private readonly TimeSpan gracePeriod;
    private readonly int exitCode;
    private readonly object gate = new();
    private System.Threading.Timer? timer;
    private bool requested;
    private bool disposed;
    private bool forced;

    internal ShutdownDeadline(Action<int>? exit = null, TimeSpan? gracePeriod = null, int exitCode = 2)
    {
        this.exit = exit ?? ProcessTermination.Exit;
        this.gracePeriod = gracePeriod ?? TimeSpan.FromSeconds(3);
        this.exitCode = exitCode;
    }

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
                timer = new System.Threading.Timer(_ => ForceExit(), null, gracePeriod, Timeout.InfiniteTimeSpan);
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
            exit(exitCode);
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
