namespace ReShadeCentralized.UiTests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, eventArgs) => FatalErrors.Terminate(eventArgs.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) => FatalErrors.Terminate(
            eventArgs.ExceptionObject as Exception ?? new Exception("An unhandled runtime failure occurred."));
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.SetColorMode(SystemInformation.HighContrast ? SystemColorMode.Classic : SystemColorMode.Dark);
            if (typeof(MainForm).Assembly == typeof(UiPreview).Assembly ||
                typeof(MainForm).Assembly.GetType("ReShadeCentralized.UiPreview") != null)
            {
                throw new InvalidOperationException("UI checks must remain outside the production assembly.");
            }
            if (args.Contains("--shutdown-probe"))
            {
                UiPreview.RunShutdownProbe(args);
            }
            else
            {
                UiPreview.Run(args);
                Console.WriteLine("UI checks passed.");
            }
            return 0;
        }
        catch (Exception error)
        {
            if (args.Contains("--shutdown-probe"))
            {
                FatalErrors.Terminate(error);
            }
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
