using System.Security.Cryptography;

namespace ReShadeCentralized;

public static class GameRemoval
{
    public static UnregisterResult Remove(
        ManagerState state,
        GameProfile game,
        FileTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ValidateProfile(state, game);
        var result = new UnregisterResult();
        var presets = Presets(state, game, result);
        var expected = GameFiles.ConfigurationPaths(game.Executable, game.Api)
            .Select(path => new OwnedLink(path, ConfigurationService.ConfigPath(game)))
            .ToList();

        if (CompatibilityCatalog.Default.Proxy(game.Executable, game.Api) is string proxy)
        {
            string runtime = game.Architecture == CpuArchitecture.X64 ? "ReShade64.dll" : "ReShade32.dll";
            expected.Add(new(
                Path.Combine(CompatibilityCatalog.Default.InstallationDirectory(game.Executable, game.Api), proxy),
                Path.Combine(state.Paths.Binaries(game.Build), runtime)));
        }

        foreach (OwnedLink link in game.Links.Concat(expected)
            .DistinctBy(link => link.Path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!Path.IsPathFullyQualified(link.Path) || !Path.IsPathFullyQualified(link.Target))
                {
                    Preserve(link.Path);
                    continue;
                }

                LocalFiles.CheckParents(link.Path);
                if (!LocalFiles.Exists(link.Path) && !Directory.Exists(link.Path))
                {
                    continue;
                }

                bool recorded = game.Links.Any(owned => LocalFiles.Same(owned.Path, link.Path)
                    && LocalFiles.Same(owned.Target, link.Target));
                bool allowed = expected.Any(known => LocalFiles.Same(known.Path, link.Path)
                    && LocalFiles.Same(known.Target, link.Target));
                bool shared = state.Games.Any(other => other.Id != game.Id
                    && other.Links.Any(owned => LocalFiles.Same(owned.Path, link.Path)));
                if (!recorded || !allowed || shared
                    || !transaction.DeleteIfUnchanged(link.Path, expectedTarget: link.Target))
                {
                    Preserve(link.Path);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or System.Security.SecurityException)
            {
                throw new IOException("Cannot safely remove managed link: " + link.Path, error);
            }
        }

        foreach ((string path, string hash) in presets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!transaction.DeleteIfUnchanged(path, expectedHash: hash))
            {
                Preserve(path);
            }
        }

        return result;

        void Preserve(string path)
        {
            if (!result.PreservedFiles.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                result.PreservedFiles.Add(path);
            }
        }
    }

    private static void ValidateProfile(ManagerState state, GameProfile game)
    {
        if (!Path.IsPathFullyQualified(state.Paths.Root)
            || !Path.IsPathFullyQualified(game.Directory)
            || !Path.IsPathFullyQualified(game.Executable)
            || !Inside(state.Paths.Profiles, game.Directory)
            || !Enum.IsDefined(game.Api)
            || !Enum.IsDefined(game.Build)
            || !Enum.IsDefined(game.Architecture))
        {
            throw new InvalidDataException(
                "Cannot safely unregister: the game's storage or installation path is invalid.");
        }

        string relative = Path.GetRelativePath(state.Paths.Profiles, game.Directory);
        if (relative.Split(['\\', '/']).Any(part => part.EndsWith('.')
            || part.EndsWith(' ')
            || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new InvalidDataException("Cannot safely unregister: the profile path is ambiguous.");
        }

        LocalFiles.CheckParents(Path.Combine(game.Directory, "check"));
        if (state.Games.Any(other => other.Id != game.Id
            && (LocalFiles.Same(other.Directory, game.Directory)
                || Inside(game.Directory, other.Directory)
                || Inside(other.Directory, game.Directory))))
        {
            throw new InvalidDataException("Cannot safely unregister: another game shares this profile folder.");
        }
    }

    private static List<(string Path, string Hash)> Presets(
        ManagerState state,
        GameProfile game,
        UnregisterResult result)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(game.Directory, "ReShadePreset.ini")
        };

        string config = ConfigurationService.ConfigPath(game);
        if (LocalFiles.Link(config) != null || Directory.Exists(config))
        {
            result.PreservedFiles.Add(config);
        }
        else if (File.Exists(config))
        {
            string? selected = ConfigurationService.SelectedPreset(IniDocument.Read(config), game.Directory);
            if (selected != null)
            {
                candidates.Add(selected);
            }
        }

        var presets = new List<(string, string)>();
        foreach (string path in candidates)
        {
            if (!Inside(game.Directory, path) || LocalFiles.Same(path, config))
            {
                result.PreservedFiles.Add(path);
                continue;
            }

            LocalFiles.CheckParents(path);
            if (LocalFiles.Link(path) != null || Directory.Exists(path))
            {
                result.PreservedFiles.Add(path);
                continue;
            }

            if (!File.Exists(path))
            {
                continue;
            }

            string extension = Path.GetExtension(path);
            if (!(extension.Equals(".ini", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)))
            {
                result.PreservedFiles.Add(path);
                continue;
            }

            byte[] contents = File.ReadAllBytes(path);
            using var reader = new StreamReader(new MemoryStream(contents));
            var preset = new IniDocument(reader.ReadToEnd());
            if ((preset.Get("", "Techniques") == null && preset.Get("", "TechniqueSorting") == null)
                || ReferencedByAnotherGame(state, game, path))
            {
                result.PreservedFiles.Add(path);
                continue;
            }

            presets.Add((path, Convert.ToHexString(SHA256.HashData(contents))));
        }

        return presets;
    }

    private static bool ReferencedByAnotherGame(ManagerState state, GameProfile game, string preset)
    {
        foreach (GameProfile other in state.Games.Where(other => other.Id != game.Id))
        {
            string config = ConfigurationService.ConfigPath(other);
            LocalFiles.CheckParents(config);
            if (LocalFiles.Link(config) != null)
            {
                return true;
            }

            string? selected = ConfigurationService.SelectedPreset(IniDocument.Read(config), other.Directory);
            if (selected != null && LocalFiles.Same(selected, preset))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Inside(string directory, string path) => Path.GetFullPath(path).StartsWith(
        Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase);
}
