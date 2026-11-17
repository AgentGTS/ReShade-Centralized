using static ReShadeCentralized.GameFiles;

namespace ReShadeCentralized;

public static class GameFiles {
    internal const string ReplacementWarning = "Replaced files will be deleted. Files that existing links point to " +
        "are kept. Unregister will not recover deleted files.";

    public static readonly string[] ProxyNames =
        ["d3d9.dll", "d3d10.dll", "d3d11.dll", "d3d12.dll", "dxgi.dll", "opengl32.dll"];

    public static string? Proxy(RenderApi api) => api switch {
        RenderApi.Direct3D9 => "d3d9.dll",
        RenderApi.DXGI => "dxgi.dll",
        RenderApi.OpenGL => "opengl32.dll",
        _ => null
    };

    internal static IEnumerable<string> InspectionPaths(string directory) =>
        ProxyNames.Append("ReShade.ini").Select(name => Path.Combine(directory, name));

    internal static string RuntimeFileName(CpuArchitecture architecture) =>
        architecture == CpuArchitecture.X64 ? "ReShade64.dll" : "ReShade32.dll";

    internal static bool IsClaimedByAnotherProfile(ManagerState state, GameProfile game, string path) =>
        state.Games.Any(other => other.Id != game.Id
            && other.Links.Any(link => LocalFiles.Same(link.Path, path)));

    internal static IEnumerable<OwnedLink> ExpectedLinks(CentralPaths paths, GameProfile game) {
        string directory = Path.GetDirectoryName(game.Executable)!;
        yield return new(Path.Combine(directory, "ReShade.ini"), ConfigurationService.ConfigPath(game));

        if (Proxy(game.Api) is string proxy)
            yield return new(Path.Combine(directory, proxy),
                Path.Combine(paths.Binaries(game.Build), RuntimeFileName(game.Architecture)));
    }

    internal static bool IsInstallationLink(CentralPaths paths, GameProfile game, OwnedLink link) {
        if (!Path.IsPathFullyQualified(link.Path) || !Path.IsPathFullyQualified(link.Target))
            return false;

        string directory = Path.GetDirectoryName(game.Executable)!;
        try {
            LocalFiles.Contained(directory, Path.GetRelativePath(directory, link.Path));
        } catch (InvalidDataException) {
            return false;
        }

        string name = Path.GetFileName(link.Path).ToLowerInvariant();
        if (name == "reshade.ini")
            return LocalFiles.Same(link.Target, ConfigurationService.ConfigPath(game));
        if (name != Proxy(game.Api) && !(game.Api == RenderApi.DXGI
            && name is "d3d10.dll" or "d3d11.dll" or "d3d12.dll"))
            return false;

        string binaries = paths.Binaries(game.Build);
        if (LocalFiles.Same(link.Target, Path.Combine(binaries, RuntimeFileName(game.Architecture))))
            return true;

        // Older installs could use a different runtime architecture in a game subfolder.
        CpuArchitecture alternate = game.Architecture == CpuArchitecture.X64 ? CpuArchitecture.X86 : CpuArchitecture.X64;
        return !LocalFiles.Same(directory, Path.GetDirectoryName(link.Path)!)
            && LocalFiles.Same(link.Target, Path.Combine(binaries, RuntimeFileName(alternate)));
    }

    public static bool IsOwnedLink(OwnedLink link) {
        string? target = LocalFiles.Link(link.Path);
        return target != null
            && LocalFiles.Same(
                Path.GetFullPath(Path.Combine(Path.GetDirectoryName(link.Path)!, target)),
                link.Target);
    }

    internal static bool IsReShadeHook(string path) {
        if (HasFileTarget(path))
            return PortableExecutable.IsReShade(path);

        string? target = LocalFiles.Link(path);
        string name = Path.GetFileName(target) ?? "";
        return target != null && (name.Equals("ReShade32.dll", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ReShade64.dll", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool HasFileTarget(string path) {
        if (LocalFiles.Link(path) == null)
            return File.Exists(path);

        // Windows can report a dangling link as an existing file. Inspect its final target before reading it.
        FileSystemInfo? target = new FileInfo(path).ResolveLinkTarget(true);
        return target != null && File.Exists(target.FullName) && !Directory.Exists(target.FullName);
    }

    internal static bool CanReplaceReShadeLink(string path) => LocalFiles.Link(path) != null
        && (Path.GetFileName(path).Equals("ReShade.ini", StringComparison.OrdinalIgnoreCase) || IsReShadeHook(path));

}

public sealed record ExistingInstallationNotice(string Message, bool BlocksInstallation);

public static class InstallationInspection {
    public static string? GetInstallationWarning(GameProfile game) {
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(game.Executable))
            issues.Add("Select this profile's game executable with Repair / edit.");
        else if (!File.Exists(game.Executable))
            issues.Add("Game executable is missing or unavailable: " + game.Executable);

        if (game.Links.Count == 0)
            issues.Add("Managed links have not been recorded for this profile.");

        foreach (OwnedLink link in game.Links) {
            try {
                LocalFiles.CheckParents(link.Path);
                if (!IsOwnedLink(link)) {
                    bool occupied = LocalFiles.Exists(link.Path) || Directory.Exists(link.Path);
                    issues.Add((occupied ? "Managed link was replaced: " : "Managed link is missing: ") + link.Path);
                } else if (!File.Exists(link.Target))
                    issues.Add("Linked file is missing or unavailable: " + link.Target);
            } catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or System.Security.SecurityException) {
                issues.Add("Cannot inspect managed link: " + link.Path + "\n" + error.Message);
            }
        }

        return issues.Count == 0 ? null : string.Join("\n", issues) +
            "\n\nUse Repair / edit to review the installation. Unrelated hook DLLs must be resolved first.";
    }

    public static ExistingInstallationNotice? InspectExistingInstallation(
        string executable,
        RenderApi? api,
        IReadOnlyList<OwnedLink> managedLinks,
        bool includePaths = true) {
        string? gamePath = PortableExecutable.GetValidatedGamePath(executable);
        return gamePath == null ? null : InspectDirectory(Path.GetDirectoryName(gamePath)!, api, managedLinks, includePaths);
    }

    internal static ExistingInstallationNotice? InspectDirectory(
        string directory,
        RenderApi? api,
        IReadOnlyList<OwnedLink> managedLinks,
        bool includePaths) {
        string? proxy = api.HasValue ? Proxy(api.Value) : null;
        var unmanaged = new List<string>();
        var changed = new List<string>();
        var otherApis = new List<string>();
        var unrelated = new List<string>();
        foreach (string path in InspectionPaths(directory)) {
            string name = Path.GetFileName(path);
            string label = includePaths ? path : name;
            if (!LocalFiles.Exists(path))
                continue;

            OwnedLink? owned = managedLinks.FirstOrDefault(link => LocalFiles.Same(link.Path, path));
            if (owned != null) {
                if (IsOwnedLink(owned))
                    continue;

                if (!CanReplaceReShadeLink(path)) {
                    changed.Add(label);
                    continue;
                }
            }

            if (name == "ReShade.ini")
                unmanaged.Add(label);
            else if (IsReShadeHook(path)) {
                unmanaged.Add(label);
                if (api.HasValue && name != proxy)
                    otherApis.Add(label);
            } else if (name == proxy)
                unrelated.Add(label);
        }

        string message;
        bool blocked = true;
        if (changed.Count > 0) {
            message = $"Files changed outside this app: {string.Join(", ", changed)}. " +
                "Restore the app's links or move the conflicting files before installing.";
        } else if (otherApis.Count > 0) {
            message = $"Existing ReShade uses another hook: {string.Join(", ", otherApis)}. " +
                "Choose its graphics API or remove that ReShade installation first.";
        } else if (unrelated.Count > 0) {
            message = $"An unrecognized DLL is using {string.Join(", ", unrelated)}. " +
                "Configure its mod to use a different supported DLL name before installing. This file will be kept.";
        } else if (unmanaged.Count > 0) {
            message = $"Existing ReShade: {string.Join(", ", unmanaged)}. " +
                "These files will become app-managed links after confirmation. " +
                ReplacementWarning;
            blocked = false;
        } else
            return null;

        if (unmanaged.Count > 0 && Directory.Exists(Path.Combine(directory, "reshade-shaders")))
            message += "\nLocal reshade-shaders remain available.";

        return new(message, blocked);
    }

}
