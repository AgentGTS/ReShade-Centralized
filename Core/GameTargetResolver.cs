using System.Diagnostics;
using System.Reflection.PortableExecutable;

namespace ReShadeCentralized;

public sealed record GameTarget(string Executable, string? Reason = null, bool IsRemix = false);

public static class GameTargetResolver
{
    public static GameTarget Resolve(string path, CancellationToken cancellationToken = default)
    {
        string? executable = PortableExecutable.GetValidatedGamePath(path);
        if (executable == null)
        {
            return new(path.Trim());
        }

        string root = Path.GetDirectoryName(executable)!;
        string original = executable;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { executable };
        string? reason = null;
        for (var depth = 0; depth < 4; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? relative = PortableExecutable.ReadResourceString(executable, 201);
            if (relative == null)
            {
                break;
            }
            try
            {
                string target = FileTransaction.Contained(root,
                    Path.GetRelativePath(root, Path.GetFullPath(relative, Path.GetDirectoryName(executable)!)));
                if (Path.IsPathRooted(relative) || PortableExecutable.GetValidatedGamePath(target) == null)
                {
                    break;
                }
                if (!visited.Add(target) || depth == 3)
                {
                    return new(original);
                }
                executable = target;
                reason = "Unreal bootstrap target";
            }
            catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or
                NotSupportedException or UnauthorizedAccessException)
            {
                break;
            }
        }

        string bridge = Path.Combine(Path.GetDirectoryName(executable)!, ".trex", "NvRemixBridge.exe");
        if (PortableExecutable.GetValidatedGamePath(bridge) != null &&
            PortableExecutable.Architecture(bridge) == CpuArchitecture.X64)
        {
            return new(bridge, "RTX Remix bridge", true);
        }
        bool isRemix = Path.GetFileName(executable).Equals("NvRemixBridge.exe", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFileName(Path.GetDirectoryName(executable)), ".trex",
                StringComparison.OrdinalIgnoreCase) &&
            PortableExecutable.Architecture(executable) == CpuArchitecture.X64;
        return new(executable, reason, isRemix);
    }

    public static bool HasDxvk(string executable)
    {
        foreach (string name in new[] { "d3d9.dll", "dxgi.dll" })
        {
            string path = Path.Combine(Path.GetDirectoryName(executable)!, name);
            try
            {
                FileTransaction.CheckParents(path);
                if (FileTransaction.Link(path) != null || !File.Exists(path))
                {
                    continue;
                }
                using FileStream stream = File.OpenRead(path);
                using var image = new PEReader(stream);
                if (image.PEHeaders.PEHeader != null &&
                    image.PEHeaders.CoffHeader.Characteristics.HasFlag(Characteristics.Dll) &&
                    PortableExecutable.Architecture(path) == PortableExecutable.Architecture(executable) &&
                    FileVersionInfo.GetVersionInfo(path).ProductName == "DXVK")
                {
                    return true;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                BadImageFormatException or ArgumentException or NotSupportedException or
                System.Security.SecurityException)
            {
                // An unreadable or unrelated neighbor is not evidence of a translation layer.
            }
        }
        return false;
    }
}
