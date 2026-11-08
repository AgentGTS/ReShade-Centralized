namespace ReShadeCentralized;

public static class PresetShaderCheck
{
    public static HashSet<string> InstalledEffects(ManagerState state, CancellationToken cancellationToken = default)
    {
        var effects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (InstalledPackage package in state.Packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = ShaderFiles.PackageDirectory(state.Paths.Shaders, package.Id);

            foreach (string key in package.Files.Keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!key.StartsWith("shaders/", StringComparison.OrdinalIgnoreCase) || !IsEffect(key))
                {
                    continue;
                }

                string path = LocalFiles.Contained(directory, key["shaders/".Length..]);
                if (File.Exists(path))
                {
                    effects.Add(Path.GetFileName(path));
                }
            }
        }

        return effects;
    }

    public static IReadOnlyDictionary<string, string> PackageSources(
        ManagerState state,
        IReadOnlyList<ShaderPackage> catalog,
        CancellationToken cancellationToken = default)
    {
        var sources = new Dictionary<string, Dictionary<string, (string Name, bool Recorded)>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (InstalledPackage package in state.Packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string key in package.Files.Keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (key.StartsWith("shaders/", StringComparison.OrdinalIgnoreCase))
                {
                    Add(key, package.Id, package.Name, package.Repository, recorded: true);
                }
            }
        }

        foreach (ShaderPackage package in catalog)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var denied = package.DeniedEffects.Select(EffectName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string effect in package.EffectFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!denied.Contains(EffectName(effect)))
                {
                    Add(effect, package.Id, package.Name, package.Repository, recorded: false);
                }
            }
        }

        var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string effect, Dictionary<string, (string Name, bool Recorded)> packages) in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ordered = packages.OrderBy(package => package.Value.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(package => package.Key, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string[] recorded = ordered.Where(package => package.Value.Recorded)
                .Select(package => package.Value.Name).ToArray();
            string[] possible = ordered.Where(package => !package.Value.Recorded)
                .Select(package => package.Value.Name).ToArray();

            if (recorded.Length > 0)
            {
                string label = recorded.Length == 1 ? "Recorded in installed package: " :
                    "Recorded in installed packages: ";
                descriptions[effect] = label + string.Join(", ", recorded);
                if (possible.Length > 0)
                {
                    descriptions[effect] += "; Other possible packages: " + string.Join(", ", possible);
                }
            }
            else
            {
                string label = possible.Length == 1 ? "Catalog package: " : "Possible packages: ";
                descriptions[effect] = label + string.Join(", ", possible);
            }
        }

        return descriptions;

        void Add(string path, string id, string name, string repository, bool recorded)
        {
            string effect = EffectName(path);
            if (!IsEffect(effect))
            {
                return;
            }

            string packageName = !string.IsNullOrWhiteSpace(name) ? name :
                !string.IsNullOrWhiteSpace(repository) ? repository :
                !string.IsNullOrWhiteSpace(id) ? id : "Unknown package";
            string identity = !string.IsNullOrWhiteSpace(id) ? id :
                !string.IsNullOrWhiteSpace(repository) ? repository : packageName;
            if (!sources.TryGetValue(effect, out Dictionary<string, (string Name, bool Recorded)>? packages))
            {
                packages = new(StringComparer.OrdinalIgnoreCase);
                sources.Add(effect, packages);
            }

            if (!packages.TryGetValue(identity, out var existing) || (recorded && !existing.Recorded))
            {
                packages[identity] = (packageName, recorded);
            }
        }
    }

    private static (string[] Missing, string[] Unknown) Inspect(
        GameProfile game,
        IReadOnlySet<string> installedEffects,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IniDocument config = IniDocument.Read(ConfigurationService.ConfigPath(game));
        string? presetPath = ConfigurationService.SelectedPreset(config, game.Directory);
        if (presetPath == null)
        {
            if (IniDocument.Split(config.Get("GENERAL", "PresetPath")).Any(path => !string.IsNullOrWhiteSpace(path)))
            {
                throw new FileNotFoundException("The selected preset file was not found.");
            }

            return ([], []);
        }

        var preset = new IniDocument(File.ReadAllText(presetPath));
        string[] techniques = IniDocument.Split(preset.Get("", "Techniques"))
            .Select(technique => technique.Trim())
            .Where(technique => technique.Length > 0)
            .ToArray();
        string[] unknown = techniques.Where(technique =>
        {
            int at = technique.IndexOf('@');
            return at <= 0 || !IsEffect(technique[(at + 1)..]);
        })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] missing = techniques.Where(technique => !unknown.Contains(technique, StringComparer.OrdinalIgnoreCase))
            .Select(technique => technique[(technique.IndexOf('@') + 1)..])
            .Where(effect => !installedEffects.Contains(effect))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(effect => effect, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return (ExcludeGameLocalEffects(game, config, missing, cancellationToken), unknown);
    }

    private static string[] ExcludeGameLocalEffects(
        GameProfile game,
        IniDocument config,
        string[] missing,
        CancellationToken cancellationToken)
    {
        if (missing.Length == 0 || !Path.IsPathFullyQualified(game.Executable))
        {
            return missing;
        }

        string[] gameDirectories = GameFiles.InstallationDirectories(game.Executable, game.Api)
            .Select(Path.GetFullPath)
            .ToArray();
        string? configuredBase = IniDocument.Split(config.Get("INSTALL", "BasePath")).FirstOrDefault();
        string baseDirectory = string.IsNullOrWhiteSpace(configuredBase)
            ? game.Directory
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredBase), game.Directory);
        var unresolved = missing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var searched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string search in IniDocument.Split(config.Get("GENERAL", "EffectSearchPaths")))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(search))
            {
                continue;
            }

            string path;
            try
            {
                path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(search), baseDirectory);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
            {
                continue;
            }

            bool recursive = Path.GetFileName(path) == "**";
            string directory = recursive ? Path.GetDirectoryName(path)! : path;
            if (!gameDirectories.Any(gameDirectory => LocalFiles.Same(directory, gameDirectory)
                    || directory.StartsWith(gameDirectory.TrimEnd('\\', '/') + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                || !searched.Add(path)
                || !Directory.Exists(directory))
            {
                continue;
            }

            try
            {
                foreach (string effect in GameLocalEffects(directory, recursive, cancellationToken))
                {
                    unresolved.Remove(effect);
                    if (unresolved.Count == 0)
                    {
                        return [];
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                System.Security.SecurityException)
            {
            }
        }

        return missing.Where(unresolved.Contains).ToArray();
    }

    private static IEnumerable<string> GameLocalEffects(
        string directory,
        bool recursive,
        CancellationToken cancellationToken)
    {
        var files = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true };
        var directories = new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true
        };
        var pending = new Stack<string>();
        pending.Push(directory);

        while (pending.TryPop(out string? current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string file in Directory.EnumerateFiles(current, "*", files))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsEffect(file))
                {
                    continue;
                }

                bool available;
                try
                {
                    available = GameFiles.HasFileTarget(file);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                    System.Security.SecurityException)
                {
                    continue;
                }

                if (available)
                {
                    yield return Path.GetFileName(file);
                }
            }

            if (recursive)
            {
                foreach (string child in Directory.EnumerateDirectories(current, "*", directories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    pending.Push(child);
                }
            }
        }
    }

    private static bool IsEffect(string path) =>
        path.EndsWith(".fx", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".addonfx", StringComparison.OrdinalIgnoreCase);

    private static string EffectName(string path)
    {
        string normalized = path.Trim().Replace('\\', '/');
        return normalized[(normalized.LastIndexOf('/') + 1)..];
    }

    public static string GetWarning(
        GameProfile game,
        IReadOnlySet<string> installedEffects,
        IReadOnlyDictionary<string, string>? packageSources = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            (string[] missing, string[] unknown) = Inspect(game, installedEffects, cancellationToken);
            var warnings = new List<string>();
            if (missing.Length > 0)
            {
                warnings.Add(
                    "Selected preset references effects missing from central packages and game-local search paths:\n" +
                    string.Join("\n", missing.Select(effect =>
                        effect + " \u2014 " +
                        (packageSources != null && packageSources.TryGetValue(EffectName(effect), out string? source)
                            ? source : "Package unknown"))));
            }

            if (unknown.Length > 0)
            {
                warnings.Add("Cannot identify the effect files for these enabled techniques:\n" +
                    string.Join("\n", unknown));
            }

            return string.Join("\n\n", warnings);
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException)
        {
            return "Unable to check the selected preset: " + error.Message;
        }
    }
}
