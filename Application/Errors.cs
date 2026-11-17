using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ReShadeCentralized;

internal static class FatalErrors {
    private const uint FileAppendData = 0x00000004;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeNormal = 0x00000080;
    private static int handling;
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "Crash.log");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string name, uint access, FileShare share, IntPtr security, FileMode creation, uint flags, IntPtr template);

    private static void AppendLog(string path, string text) {
        LocalFiles.CheckParents(path);
        using SafeFileHandle handle = CreateFileW(path, FileAppendData, FileShare.Read, IntPtr.Zero,
            FileMode.OpenOrCreate, FileFlagOpenReparsePoint | FileAttributeNormal, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        if ((File.GetAttributes(handle) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Crash log is a reparse point.");

        LocalFiles.EnsureSingleLink(handle);

        using var stream = new FileStream(handle, FileAccess.Write);
        using var writer = new StreamWriter(stream);
        writer.Write(text);
    }

    internal static void Terminate(Exception error) {
        if (Interlocked.Exchange(ref handling, 1) != 0)
            ProcessTermination.Exit(1);

        using var deadline = new ShutdownDeadline(exitCode: 1);
        deadline.Request();

        try {
            AppendLog(LogPath, DateTimeOffset.Now.ToString("O") + Environment.NewLine + error +
                Environment.NewLine + Environment.NewLine);
        } catch (Exception) {
            // Logging must never prevent fatal termination, including when the app drive is unavailable.
        } finally {
            // Do not resume an application after an unhandled failure. Pending journals remain recoverable.
            ProcessTermination.Exit(1);
        }
    }
}

public static class UserErrors {
    public static string Describe(Exception error) {
        string? instruction = error switch {
            HttpRequestException { StatusCode: HttpStatusCode.NotFound } =>
                "The download is no longer available. For shader packages, open Shader packages > " +
                "Refresh catalog and retry. For ReShade, use Runtime & storage > Import downloaded installers.",
            HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests } =>
                "The server refused the download or limited requests. Wait before retrying and check " +
                "whether your network permits access to the download site.",
            HttpRequestException =>
                "Check your internet connection and access to the download site, then retry the action.",
            OperationCanceledException =>
                "The download timed out. Check your internet connection and retry the action.",
            UnauthorizedAccessException =>
                "Run ReShade Centralized as administrator and check write permissions for the application " +
                "and central folders shown in Runtime & storage.",
            IOException when (error.HResult & 0xFFFF) is 32 or 33 =>
                "A file is in use. Close affected games and any editor using these files, then retry.",
            IOException when (error.HResult & 0xFFFF) is 39 or 112 =>
                "Free disk space on the drives containing the application and central folders, then retry.",
            _ => null
        };

        return instruction == null ? error.Message : $"{instruction}\n\nDetails: {error.Message}";
    }
}
