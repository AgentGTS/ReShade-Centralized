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

    private static (string[] Missing, string[] Unknown) Inspect(GameProfile game, IReadOnlySet<string> installedEffects)
    {
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

        return (missing, unknown);
    }

    private static bool IsEffect(string path) =>
        path.EndsWith(".fx", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".addonfx", StringComparison.OrdinalIgnoreCase);

    public static string GetWarning(GameProfile game, IReadOnlySet<string> installedEffects)
    {
        try
        {
            (string[] missing, string[] unknown) = Inspect(game, installedEffects);
            var warnings = new List<string>();
            if (missing.Length > 0)
            {
                warnings.Add("Selected preset references effects missing from central shader packages:\n" +
                    string.Join("\n", missing));
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
