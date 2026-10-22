using ReShadeCentralized;

namespace ReShade_Centralized;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemInformation.HighContrast ? SystemColorMode.Classic : SystemColorMode.Dark);
        if (args.Contains("--smoke-test"))
        {
            try
            {
                UiPreview.Run(args);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                Environment.ExitCode = 1;
            }

            return;
        }

        using var instance = new Mutex(true, "Local\\ReShadeCentralized.Manager", out bool first);
        if (!first)
        {
            MessageBox.Show("ReShade Centralized is already running.");
            return;
        }

        Application.Run(new MainForm(AppContext.BaseDirectory));
    }
}
