using ReShadeCentralized;

namespace ReShade_Centralized;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, eventArgs) => FatalErrors.Terminate(eventArgs.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) => FatalErrors.Terminate(
            eventArgs.ExceptionObject as Exception ?? new Exception("An unhandled runtime failure occurred."));
        try
        {
            Run();
        }
        catch (Exception error)
        {
            FatalErrors.Terminate(error);
        }
    }

    private static void Run()
    {
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemInformation.HighContrast ? SystemColorMode.Classic : SystemColorMode.Dark);
        using var instance = new Mutex(true, "Local\\ReShadeCentralized.Manager", out bool first);
        if (!first)
        {
            MessageBox.Show("ReShade Centralized is already running.");
            return;
        }

        Application.Run(new MainForm(AppContext.BaseDirectory));
    }
}
