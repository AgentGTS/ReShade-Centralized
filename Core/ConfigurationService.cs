namespace ReShadeCentralized;

public static class ConfigurationService
{
    public static string ConfigPath(GameProfile game) => Path.Combine(game.Directory, "ReShade.ini");

    public static IEnumerable<string> SearchPaths(ManagerState state, bool textures)
    {
        string root = textures ? state.Paths.Textures : state.Paths.Shaders;
        // Preserve the old flat tree and the user's development tree during migration.
        yield return root;
        yield return Path.Combine(root, "development", "**");
        foreach (InstalledPackage? package in state.Packages.OrderBy(package => package.Id, StringComparer.Ordinal))
        {
            string directory = ShaderService.PackageDirectory(root, package.Id);
            if (Directory.Exists(directory))
            {
                yield return Path.Combine(directory, "**");
            }
        }
    }

    private static string ManagedList(string? existing, IEnumerable<string> desired, IEnumerable<string>? previous)
    {
        // Keep explicit custom search paths; replace only paths previously managed by this app.
        var managedPaths = (previous ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var customPaths = IniDocument.Split(existing).Where(path => path.Length > 0 && !managedPaths.Contains(path));
        return IniDocument.Join(desired.Concat(customPaths).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string RelocatedPath(string path, ManagerState? previous, ManagerState state)
    {
        if (previous == null || string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        foreach ((string? source, string? target) in previous.Paths.Directories().Zip(state.Paths.Directories()))
        {
            if (FileTransaction.Same(path, source))
            {
                return target;
            }

            if (path.StartsWith(
                source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(target, Path.GetRelativePath(source, path));
            }
        }

        return path;
    }

    public static void Update(
        ManagerState state,
        GameProfile game,
        FileTransaction transaction,
        ManagerState? previous = null,
        string? initialText = null)
    {
        string path = ConfigPath(game);
        Directory.CreateDirectory(Path.Combine(state.Paths.Shaders, "development"));
        Directory.CreateDirectory(Path.Combine(state.Paths.Textures, "development"));
        IniDocument document = LoadConfiguration(path, initialText, state.ConfigurationTemplate);
        string originalBase = game.Executable == null ? game.Directory : Path.GetDirectoryName(game.Executable)!;
        string? baseOverride = IniDocument.Split(document.Get("INSTALL", "BasePath")).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(baseOverride))
        {
            originalBase = Path.GetFullPath(baseOverride, originalBase);
        }

        // Once BasePath points at the central profile, preserve the meaning of old relative paths.
        foreach ((string? section, string? key) in new[]
        {
            ("GENERAL", "EffectSearchPaths"),
            ("GENERAL", "TextureSearchPaths"),
            ("GENERAL", "PresetPath"),
            ("ADDON", "AddonPath")
        })
        {
            string? value = document.Get(section, key);
            if (value != null && !value.Contains("DO NOT EDIT"))
            {
                var resolvedPaths = IniDocument.Split(value).Select(searchPath =>
                {
                    string absolutePath = string.IsNullOrWhiteSpace(searchPath) || Path.IsPathFullyQualified(searchPath)
                        ? searchPath
                        : Path.GetFullPath(searchPath, originalBase);
                    return RelocatedPath(absolutePath, previous, state);
                });
                document.Set(section, key, IniDocument.Join(resolvedPaths));
            }
        }

        document.Set("INSTALL", "BasePath", IniDocument.Join([game.Directory]));
        document.Set(
            "GENERAL",
            "EffectSearchPaths",
            ManagedList(
                document.Get("GENERAL", "EffectSearchPaths"),
                SearchPaths(state, false),
                SearchPaths(previous ?? state, false)));
        document.Set(
            "GENERAL",
            "TextureSearchPaths",
            ManagedList(
                document.Get("GENERAL", "TextureSearchPaths"),
                SearchPaths(state, true),
                SearchPaths(previous ?? state, true)));
        document.Set("GENERAL", "IntermediateCachePath", IniDocument.Join([Path.Combine(game.Directory, "Cache")]));
        string preset = Path.Combine(game.Directory, "ReShadePreset.ini");
        if (!File.Exists(preset))
        {
            transaction.WriteText(preset, "PreprocessorDefinitions=\nTechniques=\nTechniqueSorting=\n");
        }

        // Preserve the user's active preset unless its former path moved with this profile.
        GameProfile? formerProfile = previous?.Games.SingleOrDefault(profile => profile.Id == game.Id);
        string? selectedPreset = document.Get("GENERAL", "PresetPath");
        bool resetPreset = selectedPreset == null || selectedPreset.Contains("DO NOT EDIT");
        if (!resetPreset && formerProfile != null)
        {
            string formerDefault = Path.Combine(formerProfile.Directory, "ReShadePreset.ini");
            resetPreset = IniDocument.Split(selectedPreset).Any(path => FileTransaction.Same(path, formerDefault));
        }

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
        transaction.WriteText(path, document.ToString());
    }

    private static IniDocument LoadConfiguration(string path, string? initialText, string? template)
    {
        if (initialText != null)
        {
            return new IniDocument(initialText);
        }

        return File.Exists(path) ? IniDocument.Read(path) : new IniDocument(template ?? "");
    }
}
