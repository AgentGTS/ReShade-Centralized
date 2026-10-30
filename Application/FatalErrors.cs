namespace ReShadeCentralized;

internal static class FatalErrors
{
    private static int handling;
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "Crash.log");

    internal static void Terminate(Exception error)
    {
        if (Interlocked.Exchange(ref handling, 1) != 0)
        {
            ProcessTermination.Exit(1);
        }
        using var deadline = new ShutdownDeadline(exitCode: 1);
        deadline.Request();
        try
        {
            File.AppendAllText(LogPath, DateTimeOffset.Now.ToString("O") + Environment.NewLine + error +
                Environment.NewLine + Environment.NewLine);
        }
        catch (Exception)
        {
            // Logging must never prevent fatal termination, including when the app drive is unavailable.
        }
        finally
        {
            // Do not resume an application after an unhandled failure. Pending journals remain recoverable.
            ProcessTermination.Exit(1);
        }
    }
}
