using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ReShadeCentralized;

public static class LocalFiles
{
    public static string HashText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static string Hash(string path) => Hash(path, CancellationToken.None);

    public static string Hash(string path, CancellationToken cancellationToken)
    {
        using FileStream stream = File.OpenRead(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024];
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, count);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    public static string? Link(string path) => new FileInfo(path).LinkTarget;

    public static bool Exists(string path) => File.Exists(path) || Link(path) != null;

    public static bool Same(string firstPath, string secondPath)
    {
        string normalizedFirst = Path.GetFullPath(firstPath).TrimEnd(Path.DirectorySeparatorChar);
        string normalizedSecond = Path.GetFullPath(secondPath).TrimEnd(Path.DirectorySeparatorChar);
        return normalizedFirst.Equals(normalizedSecond, StringComparison.OrdinalIgnoreCase);
    }

    public static string Contained(string root, string relative)
    {
        if (Path.IsPathRooted(relative))
        {
            throw new InvalidDataException("Absolute archive/file entry rejected.");
        }

        foreach (string part in relative.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            string device = part.Split('.')[0].TrimEnd(' ');
            bool numberedDevice = device.Length == 4
                && (device.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || device.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && (device[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3');
            if (part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || numberedDevice || device.Equals("CON", StringComparison.OrdinalIgnoreCase)
                || device.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || device.Equals("AUX", StringComparison.OrdinalIgnoreCase)
                || device.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || device.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
                || device.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Unsafe archive/file entry: " + relative);
            }
        }

        string path = Path.GetFullPath(Path.Combine(root, relative));
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Path escapes its destination.");
        }

        return path;
    }

    public static void CheckParents(string path)
    {
        for (DirectoryInfo? parent = Directory.GetParent(Path.GetFullPath(path));
            parent != null;
            parent = parent.Parent)
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(parent.FullName);
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("A destination parent is a reparse point: " + parent.FullName);
            }
        }
    }

    internal static void EnsureSingleLink(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, 1, out FileStandardInformation information,
                (uint)Marshal.SizeOf<FileStandardInformation>()))
        {
            throw new IOException("Cannot verify the file's hard-link count.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        if (information.NumberOfLinks != 1 || information.Directory != 0)
        {
            throw new IOException("A file with multiple hard links or an invalid link count cannot be modified safely.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInformation
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int informationClass,
        out FileStandardInformation information,
        uint size);

    internal static void AtomicMove(string source, string target)
    {
        // Virus scanners can briefly hold newly downloaded files and frequently written
        // journals. Bounded retries also leave genuinely locked game DLLs recoverable.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(source, target, true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                int code = e.HResult & 0xffff;
                if (attempt >= 5 || code is not (5 or 32 or 33))
                {
                    throw new IOException(
                        "Could not replace " + target +
                        ". Close affected games and check write permissions.",
                        e);
                }

                Thread.Sleep(25 << attempt);
            }
        }
    }

}
