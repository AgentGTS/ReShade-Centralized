using System.Security.Cryptography;

namespace ReShadeCentralized;

public static class LocalFiles
{
    public static string Hash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
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
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("A destination parent is a reparse point: " + parent.FullName);
            }
        }
    }

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
