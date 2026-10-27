using System.Security.Cryptography;
using System.Text;

namespace ReShadeCentralized;

public sealed class DeploymentService
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

    private static string[] InstallationDirectories(string executable, RenderApi? api) =>
        new[] { Path.GetDirectoryName(executable)!, CompatibilityCatalog.Default.InstallationDirectory(executable, api) }
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static IEnumerable<string> InspectionPaths(string executable, RenderApi? api) =>
        InstallationDirectories(executable, api).SelectMany(directory =>
            ProxyNames.Append("ReShade.ini").Select(name => Path.Combine(directory, name)));

    private static string? ExistingConfiguration(string executable, RenderApi api)
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

    private static IniDocument ReadAdoptedConfiguration(string source)
    {
        IniDocument document = IniDocument.Read(source);
        document.SetDefault("INSTALL", "BasePath", IniDocument.Join([Path.GetDirectoryName(source)!]));
        return document;
    }

    public static bool IsOwnedLink(OwnedLink link)
    {
        string? target = FileTransaction.Link(link.Path);
        return target != null &&
            FileTransaction.Same(
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(link.Path)!, target)),
            link.Target);
    }

    private static bool IsReShadeHook(string path)
    {
        if (HasFileTarget(path))
        {
            return PortableExecutable.IsReShade(path);
        }

        string? target = FileTransaction.Link(path);
        string name = Path.GetFileName(target) ?? "";
        return target != null && (name.Equals("ReShade32.dll", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("ReShade64.dll", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasFileTarget(string path)
    {
        if (FileTransaction.Link(path) == null)
        {
            return File.Exists(path);
        }

        // Windows can report a dangling link as an existing file. Inspect its final target before reading it.
        FileSystemInfo? target = new FileInfo(path).ResolveLinkTarget(true);
        return target != null && File.Exists(target.FullName) && !Directory.Exists(target.FullName);
    }

    private static bool CanReplaceReShadeLink(string path) => FileTransaction.Link(path) != null &&
        (Path.GetFileName(path).Equals("ReShade.ini", StringComparison.OrdinalIgnoreCase) || IsReShadeHook(path));

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
                FileTransaction.CheckParents(link.Path);
                if (!IsOwnedLink(link))
                {
                    bool occupied = FileTransaction.Exists(link.Path) || Directory.Exists(link.Path);
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
            if (!FileTransaction.Exists(path))
            {
                continue;
            }

            OwnedLink? owned = managedLinks.FirstOrDefault(link => FileTransaction.Same(link.Path, path));
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
                if (api.HasValue && (name != proxy || !FileTransaction.Same(Path.GetDirectoryName(path)!, directory)))
                {
                    otherApis.Add(label);
                }
            }
            else if (name == proxy && FileTransaction.Same(Path.GetDirectoryName(path)!, directory))
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

    public static InstallationPlan CreatePlan(ManagerState state, GameProfile game)
    {
        if (string.IsNullOrWhiteSpace(game.Executable) || !File.Exists(game.Executable))
        {
            throw new FileNotFoundException("The game executable is missing. " +
                "Open Games > Install game or Repair / edit and select its Executable.");
        }

        string selected = PortableExecutable.GetValidatedGamePath(game.Executable) ??
            throw new IOException("Choose a valid local x86/x64 game executable outside Windows folders.");
        GameTarget target = GameTargetResolver.Resolve(selected);
        string executable = target.Executable;
        string directory = CompatibilityCatalog.Default.InstallationDirectory(executable, game.Api);
        FileTransaction.CheckParents(Path.Combine(directory, "ReShade.ini"));
        if (!Directory.Exists(directory))
        {
            throw new IOException("The compatibility install folder is missing: " + directory +
                ". Select the actual game executable or restore this game folder before installing.");
        }
        string profile = game.Directory.Length == 0 ?
            ProfilePaths.DirectoryFor(state, game, executable) : game.Directory;
        string centralConfig = Path.Combine(profile, "ReShade.ini");
        ExistingInstallationNotice? notice =
            InspectExistingInstallation(executable, game.Api, game.Links);
        string? conflict = notice?.BlocksInstallation == true ? notice.Message : null;
        var links = new List<InstallationLink>();
        var observations = new List<string> { executable, game.Api.ToString(), game.Build.ToString(), profile };
        string? proxy = CompatibilityCatalog.Default.Proxy(executable, game.Api);
        CpuArchitecture architecture = CompatibilityCatalog.Default.Architecture(executable, game.Api);
        string stem = architecture == CpuArchitecture.X64 ?
            "ReShade64.dll" : "ReShade32.dll";
        if (proxy != null)
        {
            AddLink(Path.Combine(directory, proxy), Path.Combine(state.Paths.Binaries(game.Build), stem));
        }

        foreach (string config in ConfigurationPaths(executable, game.Api))
        {
            AddLink(config, centralConfig);
        }
        string[] removedLinks = game.Links.Where(old => !links.Any(link => FileTransaction.Same(link.Path, old.Path)))
            .Select(link => link.Path).ToArray();
        foreach (OwnedLink old in game.Links)
        {
            Observe(old.Path);
            if (FileTransaction.Exists(old.Path) && !IsOwnedLink(old) && !CanReplaceReShadeLink(old.Path))
            {
                conflict = "Managed files were changed outside this app. " +
                    "Resolve this path before installing: " + old.Path;
            }
        }

        foreach (string path in InspectionPaths(executable, game.Api))
        {
            Observe(path);
        }

        string? preset = null;
        bool adopting = !File.Exists(centralConfig);
        string? source = adopting ? ExistingConfiguration(executable, game.Api) : null;
        if (source != null)
        {
            observations.Add("Configuration source: " + source);
            Observe(source);
            preset = ConfigurationService.SelectedPreset(ReadAdoptedConfiguration(source), Path.GetDirectoryName(source)!);
            if (preset != null)
            {
                // Opening the file here makes unreadable selections fail before replacing any game files.
                observations.Add(preset + " preset:" + FileTransaction.Hash(preset));
            }
        }

        string[] localPaths = ConfigurationService.LocalSearchPaths(game, false)
            .Concat(ConfigurationService.LocalSearchPaths(game, true)).ToArray();
        observations.AddRange(localPaths);
        observations.Add(preset ?? "");
        byte[] fingerprintBytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", observations)));
        string fingerprint = Convert.ToHexString(fingerprintBytes);
        return new(directory, profile, links, removedLinks, localPaths, preset, fingerprint, conflict)
        {
            Executable = executable,
            TargetReason = target.Reason,
            ConfigurationSource = source,
            RuntimeArchitecture = architecture != PortableExecutable.Architecture(executable) ? architecture : null
        };

        void AddLink(string path, string target)
        {
            observations.Add(path + " -> " + target);
            bool owned = game.Links.Any(link => FileTransaction.Same(link.Path, path) && IsOwnedLink(link));
            if (!owned && FileTransaction.Link(path) != null && !CanReplaceReShadeLink(path))
            {
                conflict = "An unmanaged symbolic link occupies this path. Resolve it before installing: " + path;
            }

            if (Directory.Exists(path))
            {
                conflict = "A directory occupies a required file path. Resolve it before installing: " + path;
            }

            links.Add(new(path, target, FileTransaction.Exists(path) && !owned,
                owned ? null : FileTransaction.Link(path)));
        }

        void Observe(string path)
        {
            string? link = FileTransaction.Link(path);
            string contents = HasFileTarget(path) ? FileTransaction.Hash(path) : "missing";
            observations.Add(path + "=" + (link != null ? "link:" + link + ":" : "") + contents);
        }
    }

    public static void ValidatePlan(InstallationPlan current, InstallationPlan? reviewed, bool replaceExisting)
    {
        if (current.Conflict != null)
        {
            throw new IOException(current.Conflict);
        }

        if ((reviewed != null && current.Fingerprint != reviewed.Fingerprint) ||
            (current.RequiresConfirmation && (reviewed == null || !replaceExisting)))
        {
            throw new IOException("Review the installation changes again and confirm replacement of existing files.");
        }
    }

    private static void Link(
        GameProfile game,
        string path,
        string target,
        FileTransaction transaction,
        bool adoptConfig = false)
    {
        OwnedLink? prior = game.Links.SingleOrDefault(link => FileTransaction.Same(link.Path, path));
        if (prior != null && FileTransaction.Exists(path) && !IsOwnedLink(prior) && !CanReplaceReShadeLink(path))
        {
            throw new IOException("Managed link was replaced externally; preserving it: " + path);
        }

        if (prior == null && FileTransaction.Exists(path) && !adoptConfig && !IsReShadeHook(path))
        {
            throw new IOException("Refusing to replace an unrelated file: " + path);
        }

        transaction.Symlink(path, target);
        game.Links.RemoveAll(link => FileTransaction.Same(link.Path, path));
        game.Links.Add(new(path, target));
    }

    public static void Install(
        ManagerState state,
        GameProfile game,
        FileTransaction transaction,
        InstallationPlan? reviewedPlan = null,
        bool replaceExisting = false)
    {
        if (!Enum.IsDefined(game.Api))
        {
            throw new NotSupportedException("Unsupported rendering API.");
        }

        if (string.IsNullOrWhiteSpace(game.Executable) || !File.Exists(game.Executable))
        {
            throw new FileNotFoundException("The game executable is missing or was not selected. " +
                "Open Games > Install game or Repair / edit and browse to the game's actual .exe file.");
        }

        if (!Path.GetExtension(game.Executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The selected file is not a Windows executable. In Games > Install game " +
                "or Repair / edit, use Executable to select the game's .exe file.");
        }

        game.Executable = GameTargetResolver.Resolve(game.Executable).Executable;
        game.Architecture = CompatibilityCatalog.Default.Architecture(game.Executable, game.Api);
        string gameDirectory = CompatibilityCatalog.Default.InstallationDirectory(game.Executable, game.Api);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (windows.Length > 0 &&
            (FileTransaction.Same(gameDirectory, windows) ||
            gameDirectory.StartsWith(windows.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
        {
            throw new IOException("Installing into Windows system directories is prohibited.");
        }

        string stem = game.Architecture == CpuArchitecture.X64 ? "ReShade64" : "ReShade32";
        string dll = Path.Combine(state.Paths.Binaries(game.Build), stem + ".dll");
        if (!PortableExecutable.IsReShade(dll) || PortableExecutable.Architecture(dll) != game.Architecture)
        {
            throw new IOException($"The {DisplayNames.For(game.Build)} runtime for {game.Architecture} " +
                "is missing or invalid. " +
                "Open Runtime & storage > Update ReShade, then retry Games > Install game or Repair / edit.");
        }

        string? proxy = CompatibilityCatalog.Default.Proxy(game.Executable, game.Api);
        foreach (string candidate in InspectionPaths(game.Executable, game.Api)
            .Where(path => ProxyNames.Contains(Path.GetFileName(path))))
        {
            if ((Path.GetFileName(candidate) != proxy ||
                !FileTransaction.Same(Path.GetDirectoryName(candidate)!, gameDirectory)) && IsReShadeHook(candidate) &&
                !game.Links.Any(link => FileTransaction.Same(link.Path, candidate) && IsOwnedLink(link)))
            {
                throw new IOException("An existing ReShade installation for another API must be removed first: " +
                    candidate);
            }
        }

        string centralConfig = ConfigurationService.ConfigPath(game);
        InstallationPlan plan = CreatePlan(state, game);
        ValidatePlan(plan, reviewedPlan, replaceExisting);
        string? initial = plan.ConfigurationSource != null ?
            ReadAdoptedConfiguration(plan.ConfigurationSource).ToString() : null;
        if (initial != null && plan.PresetSource != null)
        {
            transaction.Copy(plan.PresetSource, Path.Combine(game.Directory, "ReShadePreset.ini"));
        }

        ConfigurationService.Update(state, game, transaction, initial);
        foreach (OwnedLink old in game.Links.ToArray())
        {
            if (plan.Links.Any(link => FileTransaction.Same(link.Path, old.Path)))
            {
                continue;
            }

            if (FileTransaction.Exists(old.Path) && !IsOwnedLink(old))
            {
                throw new IOException("Cannot remove a replaced managed link: " + old.Path);
            }

            transaction.Delete(old.Path);
            game.Links.Remove(old);
        }

        if (proxy != null)
        {
            Link(game, Path.Combine(gameDirectory, proxy), dll, transaction);
        }

        foreach (string config in ConfigurationPaths(game.Executable, game.Api))
        {
            Link(game, config, centralConfig, transaction, true);
        }
    }

    public static UnregisterResult Remove(GameProfile game, FileTransaction transaction)
    {
        var result = new UnregisterResult();
        foreach (OwnedLink link in game.Links)
        {
            if (!FileTransaction.Exists(link.Path) && !Directory.Exists(link.Path))
            {
                continue;
            }

            if (!IsOwnedLink(link))
            {
                result.PreservedFiles.Add(link.Path);
                continue;
            }

            transaction.Delete(link.Path);
        }

        return result;
    }
}
