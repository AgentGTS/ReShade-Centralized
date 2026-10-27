using System.Runtime.InteropServices;

namespace ReShadeCentralized;

internal static class ProcessTermination
{
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    internal static void Exit(int exitCode)
    {
        // The pseudo-handle always identifies this process; this path cannot target another application.
        // Bypass managed shutdown callbacks, which may themselves be the cause of a fatal shutdown stall.
        if (!TerminateProcess(GetCurrentProcess(), unchecked((uint)exitCode)))
        {
            Environment.FailFast("Windows could not terminate ReShade Centralized after an exit request.");
        }
    }
}
