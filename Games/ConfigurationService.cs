namespace ReShadeCentralized;

public static class ConfigurationService
{
    public static string ConfigPath(GameProfile game) => Path.Combine(game.Directory, "ReShade.ini");

    public static string? SelectedPreset(IniDocument document, string gameDirectory)
    {
        string? selected = IniDocument.Split(document.Get("GENERAL", "PresetPath")).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(selected))
        {
            return null;
        }

        string? basePath = IniDocument.Split(document.Get("INSTALL", "BasePath")).FirstOrDefault();
        string directory = string.IsNullOrWhiteSpace(basePath) ? gameDirectory :
            Path.GetFullPath(basePath, gameDirectory);
        string path = Path.GetFullPath(selected, directory);
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
            {
                return null;
            }
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        return File.Exists(path) ? path : null;
    }

    public static IEnumerable<string> LocalSearchPaths(GameProfile game, bool textures)
    {
        if (string.IsNullOrWhiteSpace(game.Executable))
        {
            yield break;
        }

        string executable = GameTargetResolver.Resolve(game.Executable).Executable;
        string[] directories = [Path.GetDirectoryName(executable)!,
            CompatibilityCatalog.Default.InstallationDirectory(executable, game.Api)];
        foreach (string directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string path = Path.Combine(directory, "reshade-shaders", textures ? "Textures" : "Shaders");
            if (Directory.Exists(path))
            {
                yield return Path.Combine(path, "**");
            }
        }
    }

    public static IEnumerable<string> SearchPaths(ManagerState state, bool textures)
    {
        string root = textures ? state.Paths.Textures : state.Paths.Shaders;
        foreach (InstalledPackage package in state.Packages.OrderBy(package => package.Id, StringComparer.Ordinal))
        {
            string directory = ShaderFiles.PackageDirectory(root, package.Id);
            if (Directory.Exists(directory))
            {
                yield return Path.Combine(directory, "**");
            }
        }
    }

    private static string ManagedList(string? existing, IEnumerable<string> desired)
    {
        // Keep custom paths alongside the current package and game-local paths.
        string[] desiredPaths = desired.ToArray();
        var recursiveRoots = desiredPaths.Where(path => path.EndsWith(Path.DirectorySeparatorChar + "**"))
            .Select(path => path[..^3]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var customPaths = IniDocument.Split(existing).Where(path => path.Length > 0 &&
            !desiredPaths.Contains(path, StringComparer.OrdinalIgnoreCase) &&
            !recursiveRoots.Contains(path.TrimEnd('\\', '/')));
        return IniDocument.Join(desiredPaths.Concat(customPaths).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    public static void Update(
        ManagerState state,
        GameProfile game,
        FileTransaction transaction,
        string? initialText = null,
        IReadOnlyList<string>? removedSearchRoots = null)
    {
        string path = ConfigPath(game);
        IniDocument document = initialText == null ? IniDocument.Read(path) : new IniDocument(initialText);
        string originalBase = CompatibilityCatalog.Default.InstallationDirectory(game.Executable, game.Api);
        string? baseOverride = IniDocument.Split(document.Get("INSTALL", "BasePath")).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(baseOverride))
        {
            originalBase = Path.GetFullPath(baseOverride, originalBase);
        }

        // Once BasePath points at the central profile, preserve the meaning of old relative paths.
        foreach (string key in new[]
        {
            "EffectSearchPaths", "TextureSearchPaths", "PresetPath", "StartupPresetPath"
        })
        {
            string? value = document.Get("GENERAL", key);
            if (value != null)
            {
                document.Set("GENERAL", key, IniDocument.Join(IniDocument.Split(value)
                    .Select(searchPath => ResolvePath(searchPath, originalBase))));
            }
        }

        string? addonPath = IniDocument.Split(document.Get("ADDON", "AddonPath")).FirstOrDefault();
        if (addonPath != null)
        {
            document.Set("ADDON", "AddonPath", IniDocument.Join([ResolvePath(addonPath, originalBase)]));
        }

        document.Set("INSTALL", "BasePath", IniDocument.Join([game.Directory]));
        foreach (bool textures in new[] { false, true })
        {
            string key = textures ? "TextureSearchPaths" : "EffectSearchPaths";
            string? existing = document.Get("GENERAL", key);
            if (removedSearchRoots != null)
            {
                existing = IniDocument.Join(IniDocument.Split(existing).Where(search =>
                    string.IsNullOrWhiteSpace(search) || !removedSearchRoots.Any(root =>
                        LocalFiles.Same(search.TrimEnd('\\', '/', '*'), root) ||
                        search.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase))));
            }
            document.Set("GENERAL", key, ManagedList(existing,
                SearchPaths(state, textures).Concat(LocalSearchPaths(game, textures))));
        }
        document.Set("GENERAL", "IntermediateCachePath", IniDocument.Join([Path.Combine(game.Directory, "Cache")]));
        string preset = Path.Combine(game.Directory, "ReShadePreset.ini");
        if (!File.Exists(preset))
        {
            transaction.WriteText(preset, "PreprocessorDefinitions=\nTechniques=\nTechniqueSorting=\n");
        }

        string? selectedPreset = document.Get("GENERAL", "PresetPath");
        bool resetPreset = initialText != null || string.IsNullOrWhiteSpace(selectedPreset);
        if (resetPreset)
        {
            document.Set("GENERAL", "PresetPath", IniDocument.Join([preset]));
        }

        string screenshots = Path.Combine(state.Paths.Screenshots, game.Id);
        Directory.CreateDirectory(Path.Combine(game.Directory, "Cache"));
        Directory.CreateDirectory(screenshots);
        document.Set("SCREENSHOT", "SavePath", IniDocument.Join([screenshots]));
        document.SetDefault("INPUT", "KeyOverlay", "36,0,0,0");
        document.SetDefault("GENERAL", "PerformanceMode", "0");
        document.SetDefault("GENERAL", "PresetTransitionDuration", "1000");
        CompatibilityCatalog.Default.ApplyDefaults(document, game.Executable);
        transaction.WriteText(path, document.ToString());
    }

    private static string ResolvePath(string path, string directory) =>
        string.IsNullOrWhiteSpace(path) || Path.IsPathFullyQualified(path) ? path : Path.GetFullPath(path, directory);

}
