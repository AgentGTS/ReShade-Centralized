using System.Diagnostics;
using System.Reflection.PortableExecutable;

namespace ReShadeCentralized;

public static partial class PortableExecutable
{
    public static string? GetValidatedGamePath(string path)
    {
        try
        {
            path = path.Trim();
            if (path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' ||
                (path[2] != '\\' && path[2] != '/') || path.AsSpan(2).Contains(':') ||
                path.IndexOfAny(['*', '?', '"', '<', '>', '|', '\0']) >= 0 ||
                !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string[] parts = path[3..].Split(['\\', '/']);
            if (parts.Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')))
            {
                return null;
            }

            path = Path.GetFullPath(path);
            string root = Path.GetPathRoot(path)!;
            if (new DriveInfo(root).DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.CDRom or
                DriveType.Ram))
            {
                return null;
            }

            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (windows.Length > 0 && path.StartsWith(windows.TrimEnd('\\', '/') + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // Check each parent before entering it so links cannot redirect inspection to another location.
            string parent = root;
            foreach (string part in parts[..^1])
            {
                parent = Path.Combine(parent, part);
                if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                {
                    return null;
                }
            }

            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new PEReader(stream);
            PEHeaders headers = reader.PEHeaders;
            Characteristics characteristics = headers.CoffHeader.Characteristics;
            if (headers.PEHeader == null || headers.CoffHeader.Machine is not (Machine.I386 or Machine.Amd64) ||
                !characteristics.HasFlag(Characteristics.ExecutableImage) || characteristics.HasFlag(Characteristics.Dll))
            {
                return null;
            }

            return path;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
            BadImageFormatException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    public static CpuArchitecture Architecture(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using var reader = new PEReader(stream);
        if (reader.PEHeaders.PEHeader == null)
        {
            throw new InvalidDataException("Not a PE executable: " + path);
        }

        return reader.PEHeaders.CoffHeader.Machine switch
        {
            Machine.I386 => CpuArchitecture.X86,
            Machine.Amd64 => CpuArchitecture.X64,
            _ => throw new NotSupportedException("Only x86 and x64 games and ReShade binaries are supported.")
        };
    }

    public static bool IsReShade(string path) =>
        File.Exists(path) &&
        FileVersionInfo.GetVersionInfo(path).ProductName == "ReShade";
}
