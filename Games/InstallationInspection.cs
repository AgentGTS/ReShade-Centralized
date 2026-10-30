using static ReShadeCentralized.GameFiles;

namespace ReShadeCentralized;

public static class InstallationInspection
{
    public static string? GetInstallationWarning(GameProfile game)
    {
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(game.Executable))
        {
            issues.Add("Select this profile's game executable with Repair / edit.");
        }
        else if (!File.Exists(game.Executable))
        {
            issues.Add("Game executable is missing or unavailable: " + game.Executable);
        }

        if (game.Links.Count == 0)
        {
            issues.Add("Managed links have not been recorded for this profile.");
        }

        foreach (OwnedLink link in game.Links)
        {
            try
            {
                LocalFiles.CheckParents(link.Path);
                if (!IsOwnedLink(link))
                {
                    bool occupied = LocalFiles.Exists(link.Path) || Directory.Exists(link.Path);
                    issues.Add((occupied ? "Managed link was replaced: " : "Managed link is missing: ") + link.Path);
                }
                else if (!File.Exists(link.Target))
                {
                    issues.Add("Linked file is missing or unavailable: " + link.Target);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
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
        bool includePaths = true)
    {
        string? gamePath = PortableExecutable.GetValidatedGamePath(executable);
        if (gamePath == null)
        {
            return null;
        }

        gamePath = GameTargetResolver.Resolve(gamePath).Executable;
        if (CompatibilityCatalog.Default.Find(gamePath)?.Banned == true)
        {
            return new("The official compatibility catalog marks this executable as blocking or banning ReShade. " +
                "Installation is unavailable; existing files are kept.", true);
        }
        string directory = CompatibilityCatalog.Default.InstallationDirectory(gamePath, api);
        string? proxy = api.HasValue ? CompatibilityCatalog.Default.Proxy(gamePath, api.Value) : null;
        var unmanaged = new List<string>();
        var changed = new List<string>();
        var otherApis = new List<string>();
        var unrelated = new List<string>();
        foreach (string path in InspectionPaths(gamePath, api))
        {
            string name = Path.GetFileName(path);
            string label = includePaths ? path : Path.GetRelativePath(Path.GetDirectoryName(gamePath)!, path);
            if (!LocalFiles.Exists(path))
            {
                continue;
            }

            OwnedLink? owned = managedLinks.FirstOrDefault(link => LocalFiles.Same(link.Path, path));
            if (owned != null)
            {
                if (!IsOwnedLink(owned) && !CanReplaceReShadeLink(path))
                {
                    changed.Add(label);
                    continue;
                }

                if (IsOwnedLink(owned))
                {
                    continue;
                }
            }

            if (name == "ReShade.ini")
            {
                unmanaged.Add(label);
            }
            else if (IsReShadeHook(path))
            {
                unmanaged.Add(label);
                if (api.HasValue && (name != proxy || !LocalFiles.Same(Path.GetDirectoryName(path)!, directory)))
                {
                    otherApis.Add(label);
                }
            }
            else if (name == proxy && LocalFiles.Same(Path.GetDirectoryName(path)!, directory))
            {
                unrelated.Add(label);
            }
        }

        string message;
        bool blocked = true;
        if (changed.Count > 0)
        {
            message = $"Files changed outside this app: {string.Join(", ", changed)}. " +
                "Restore the app's links or move the conflicting files before installing.";
        }
        else if (otherApis.Count > 0)
        {
            message = $"Existing ReShade uses another hook: {string.Join(", ", otherApis)}. " +
                "Choose its graphics API or remove that ReShade installation first.";
        }
        else if (unrelated.Count > 0)
        {
            message = $"An unrecognized DLL is using {string.Join(", ", unrelated)}. " +
                "Configure its mod to use a different supported DLL name before installing. This file will be kept.";
        }
        else if (unmanaged.Count > 0)
        {
            message = $"Existing ReShade: {string.Join(", ", unmanaged)}. " +
                "These files will become app-managed links after confirmation. " +
                "Replaced files will be deleted. Files that existing links point to are kept. " +
                "Unregister will not recover deleted files.";
            blocked = false;
        }
        else
        {
            return null;
        }

        if (unmanaged.Count > 0 && Directory.Exists(Path.Combine(directory, "reshade-shaders")))
        {
            message += "\nLocal reshade-shaders remain available.";
        }

        return new(message, blocked);
    }

}
