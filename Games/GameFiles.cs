namespace ReShadeCentralized;

public static class GameFiles
{
    public static readonly string[] ProxyNames =
        ["d3d9.dll", "d3d10.dll", "d3d11.dll", "d3d12.dll", "dxgi.dll", "opengl32.dll"];

    public static string? Proxy(RenderApi api) => api switch
    {
        RenderApi.Direct3D9 => "d3d9.dll",
        RenderApi.DXGI => "dxgi.dll",
        RenderApi.OpenGL => "opengl32.dll",
        _ => null
    };

    public static string[] ConfigurationPaths(string executable, RenderApi api) =>
        InstallationDirectories(executable, api).Select(directory => Path.Combine(directory, "ReShade.ini")).ToArray();

    internal static string[] InstallationDirectories(string executable, RenderApi? api) =>
        new[] { Path.GetDirectoryName(executable)!, CompatibilityCatalog.Default.InstallationDirectory(executable, api) }
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal static IEnumerable<string> InspectionPaths(string executable, RenderApi? api) =>
        InstallationDirectories(executable, api).SelectMany(directory =>
            ProxyNames.Append("ReShade.ini").Select(name => Path.Combine(directory, name)));

    internal static string? ExistingConfiguration(string executable, RenderApi api)
    {
        string directory = CompatibilityCatalog.Default.InstallationDirectory(executable, api);
        string rootConfig = Path.Combine(Path.GetDirectoryName(executable)!, "ReShade.ini");
        if (HasFileTarget(rootConfig))
        {
            string? basePath = IniDocument.Split(IniDocument.Read(rootConfig).Get("INSTALL", "BasePath")).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(basePath))
            {
                string candidate = Path.GetFullPath(basePath, directory);
                if (Directory.Exists(candidate))
                {
                    directory = candidate;
                }
            }
        }
        string config = Path.Combine(directory, "ReShade.ini");
        return HasFileTarget(config) ? config : HasFileTarget(rootConfig) ? rootConfig : null;
    }

    internal static IniDocument ReadAdoptedConfiguration(string source)
    {
        IniDocument document = IniDocument.Read(source);
        document.SetDefault("INSTALL", "BasePath", IniDocument.Join([Path.GetDirectoryName(source)!]));
        return document;
    }

    public static bool IsOwnedLink(OwnedLink link)
    {
        string? target = LocalFiles.Link(link.Path);
        return target != null &&
            LocalFiles.Same(
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(link.Path)!, target)),
            link.Target);
    }

    internal static bool IsReShadeHook(string path)
    {
        if (HasFileTarget(path))
        {
            return PortableExecutable.IsReShade(path);
        }

        string? target = LocalFiles.Link(path);
        string name = Path.GetFileName(target) ?? "";
        return target != null && (name.Equals("ReShade32.dll", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("ReShade64.dll", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool HasFileTarget(string path)
    {
        if (LocalFiles.Link(path) == null)
        {
            return File.Exists(path);
        }

        // Windows can report a dangling link as an existing file. Inspect its final target before reading it.
        FileSystemInfo? target = new FileInfo(path).ResolveLinkTarget(true);
        return target != null && File.Exists(target.FullName) && !Directory.Exists(target.FullName);
    }

    internal static bool CanReplaceReShadeLink(string path) => LocalFiles.Link(path) != null &&
        (Path.GetFileName(path).Equals("ReShade.ini", StringComparison.OrdinalIgnoreCase) || IsReShadeHook(path));

}
