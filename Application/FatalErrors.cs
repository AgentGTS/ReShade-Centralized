using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ReShadeCentralized;

internal static class FatalErrors
{
    private const uint FileAppendData = 0x00000004;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeNormal = 0x00000080;
    private static int handling;
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "Crash.log");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string name, uint access, FileShare share, IntPtr security, FileMode creation, uint flags, IntPtr template);

    private static void AppendLog(string path, string text)
    {
        LocalFiles.CheckParents(path);
        using SafeFileHandle handle = CreateFileW(path, FileAppendData, FileShare.Read, IntPtr.Zero,
            FileMode.OpenOrCreate, FileFlagOpenReparsePoint | FileAttributeNormal, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        if ((File.GetAttributes(handle) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Crash log is a reparse point.");
        }

        LocalFiles.EnsureSingleLink(handle);

        using var stream = new FileStream(handle, FileAccess.Write);
        using var writer = new StreamWriter(stream);
        writer.Write(text);
    }

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
            AppendLog(LogPath, DateTimeOffset.Now.ToString("O") + Environment.NewLine + error +
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
