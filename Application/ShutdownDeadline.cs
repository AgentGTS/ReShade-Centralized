using System.Runtime.InteropServices;

namespace ReShadeCentralized;

internal sealed class ShutdownDeadline(int exitCode = 2) : IDisposable {
    private readonly object gate = new();
    private System.Threading.Timer? timer;
    private ShutdownState state;

    private enum ShutdownState {
        Idle,
        Requested,
        Forced,
        Disposed
    }

    internal void Request() {
        bool force;
        lock (gate) {
            if (state is ShutdownState.Disposed or ShutdownState.Forced)
                return;

            force = state == ShutdownState.Requested;
            if (!force) {
                state = ShutdownState.Requested;
                // A UI timer could never fire if the window thread stalls during shutdown.
                timer = new System.Threading.Timer(
                    _ => ForceExit(),
                    null,
                    TimeSpan.FromSeconds(3),
                    Timeout.InfiniteTimeSpan);
            }
        }

        if (force)
            ForceExit();
    }

    private void ForceExit() {
        lock (gate) {
            if (state is ShutdownState.Disposed or ShutdownState.Forced)
                return;

            state = ShutdownState.Forced;
            ProcessTermination.Exit(exitCode);
        }
    }

    public void Dispose() {
        lock (gate) {
            state = ShutdownState.Disposed;
            timer?.Dispose();
            timer = null;
        }
    }
}

internal static class ProcessTermination {
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    internal static void Exit(int exitCode) {
        // The pseudo-handle always identifies this process; this path cannot target another application.
        // Bypass managed shutdown callbacks, which may themselves be the cause of a fatal shutdown stall.
        if (!TerminateProcess(GetCurrentProcess(), unchecked((uint)exitCode)))
            Environment.FailFast("Windows could not terminate ReShade Centralized after an exit request.");
    }
}
