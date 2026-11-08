using System.Text;

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
        var recursiveRoots = desiredPaths
            .Where(path => path.EndsWith(Path.DirectorySeparatorChar + "**"))
            .Select(path => path[..^3])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var customPaths = IniDocument.Split(existing)
            .Where(path => path.Length > 0
                && !desiredPaths.Contains(path, StringComparer.OrdinalIgnoreCase)
                && !recursiveRoots.Contains(path.TrimEnd('\\', '/')));

        return IniDocument.Join(desiredPaths.Concat(customPaths).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    public static void Update(
        ManagerState state,
        GameProfile game,
        FileTransaction transaction,
        string? initialText = null,
        IReadOnlyList<string>? removedSearchRoots = null)
    {
        if (!Path.IsPathFullyQualified(game.Directory))
        {
            throw new InvalidDataException("The game profile must be inside central storage.");
        }
        LocalFiles.Contained(state.Paths.Profiles, Path.GetRelativePath(state.Paths.Profiles, game.Directory));
        string path = ConfigPath(game);
        CheckProfileFile(path);
        string preset = Path.Combine(game.Directory, "ReShadePreset.ini");
        CheckProfileFile(preset);
        byte[]? original = File.Exists(path) ? ReadConfigurationBytes(path) : null;
        string? expectedHash = original == null ? null : Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(original));
        string text = original == null ? "" : ReadConfigurationText(original);
        IniDocument document = new(initialText ?? text);
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
                        LocalFiles.Same(search.TrimEnd('\\', '/', '*'), root)
                        || search.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase))));
            }
            document.Set("GENERAL", key, ManagedList(existing,
                SearchPaths(state, textures).Concat(LocalSearchPaths(game, textures))));
        }
        document.Set("GENERAL", "IntermediateCachePath", IniDocument.Join([Path.Combine(game.Directory, "Cache")]));
        if (!File.Exists(preset))
        {
            if (!transaction.WriteTextIfAbsent(preset, "PreprocessorDefinitions=\nTechniques=\nTechniqueSorting=\n"))
            {
                throw new IOException("The default preset appeared during this operation; it was preserved: " + preset);
            }
        }

        string? selectedPreset = document.Get("GENERAL", "PresetPath");
        bool resetPreset = initialText != null || string.IsNullOrWhiteSpace(selectedPreset);
        if (resetPreset)
        {
            document.Set("GENERAL", "PresetPath", IniDocument.Join([preset]));
        }

        string screenshots = LocalFiles.Contained(state.Paths.Screenshots, game.Id);
        string cache = Path.Combine(game.Directory, "Cache");
        LocalFiles.CheckParents(Path.Combine(cache, "check"));
        LocalFiles.CheckParents(Path.Combine(screenshots, "check"));
        Directory.CreateDirectory(cache);
        Directory.CreateDirectory(screenshots);
        document.Set("SCREENSHOT", "SavePath", IniDocument.Join([screenshots]));
        document.SetDefault("INPUT", "KeyOverlay", "36,0,0,0");
        document.SetDefault("GENERAL", "PerformanceMode", "0");
        document.SetDefault("GENERAL", "PresetTransitionDuration", "1000");
        CompatibilityCatalog.Default.ApplyDefaults(document, game.Executable);
        bool saved = expectedHash == null
            ? transaction.WriteTextIfAbsent(path, document.ToString())
            : transaction.RewriteIfUnchanged(path, expectedHash, document.ToString());
        if (!saved)
        {
            throw new IOException("The profile configuration changed during this operation; it was preserved: " + path);
        }
    }

    internal static byte[] ReadConfigurationBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("The configuration is too large; it was preserved: " + path);
        }
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
        {
            throw new IOException("The configuration changed while it was being read: " + path);
        }
        return bytes;
    }

    internal static string ReadConfigurationText(byte[] bytes)
    {
        ReadOnlySpan<byte> content = bytes;
        Encoding encoding = new UTF8Encoding(false, true);
        int offset = 0;
        if (content.StartsWith(new byte[] { 0xff, 0xfe, 0x00, 0x00 }))
        {
            encoding = new UTF32Encoding(false, false, true);
            offset = 4;
        }
        else if (content.StartsWith(new byte[] { 0x00, 0x00, 0xfe, 0xff }))
        {
            encoding = new UTF32Encoding(true, false, true);
            offset = 4;
        }
        else if (content.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
        {
            offset = 3;
        }
        else if (content.StartsWith(new byte[] { 0xff, 0xfe }))
        {
            encoding = new UnicodeEncoding(false, false, true);
            offset = 2;
        }
        else if (content.StartsWith(new byte[] { 0xfe, 0xff }))
        {
            encoding = new UnicodeEncoding(true, false, true);
            offset = 2;
        }

        try
        {
            string text = encoding.GetString(content[offset..]);
            if (text.Contains('\0'))
            {
                throw new InvalidDataException("The configuration contains binary data; it was preserved.");
            }
            return text;
        }
        catch (DecoderFallbackException error)
        {
            throw new InvalidDataException("The configuration text encoding is invalid; it was preserved.", error);
        }
    }

    private static void CheckProfileFile(string path)
    {
        LocalFiles.CheckParents(path);
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new IOException("A profile configuration or default preset must be a regular file: " + path);
            }
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
        }
    }

    private static string ResolvePath(string path, string directory) =>
        string.IsNullOrWhiteSpace(path) || Path.IsPathFullyQualified(path) ? path : Path.GetFullPath(path, directory);

}
