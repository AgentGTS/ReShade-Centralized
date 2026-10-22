namespace ReShadeCentralized;

public static class MigrationService
{
    public static ManagerState Create(string root, string? legacyIni, FileTransaction transaction)
    {
        var paths = CentralPaths.At(root);
        if (legacyIni != null)
        {
            var legacy = IniDocument.Read(legacyIni);
            transaction.Capture(legacyIni);
            string Read(string key, string fallback)
            {
                string? value = legacy.Get("paths", key);
                return string.IsNullOrWhiteSpace(value) ?
                    fallback :
                    Path.GetFullPath(value, Path.GetDirectoryName(legacyIni)!);
            }

            paths.Shaders = Read("shaders", paths.Shaders);
            paths.Textures = Read("textures", paths.Textures);
            paths.Profiles = Read("presets", paths.Profiles);
            paths.Screenshots = Read("screenshots", paths.Screenshots);
            paths.StandardBinaries = Read("dlls", paths.StandardBinaries);
            paths.AddonBinaries = Read("mdlls", paths.AddonBinaries);
        }

        var state = new ManagerState
        {
            Paths = paths
        };
        foreach (string path in paths.Directories())
        {
            FileTransaction.CheckParents(Path.Combine(path, "check"));
            Directory.CreateDirectory(path);
        }

        Directory.CreateDirectory(Path.Combine(paths.Shaders, "development"));
        Directory.CreateDirectory(Path.Combine(paths.Textures, "development"));
        if (legacyIni != null)
        {
            foreach (string config in ProfileConfigurations(paths.Profiles))
            {
                transaction.Capture(config);
                var game = new GameProfile
                {
                    Name = Path.GetRelativePath(paths.Profiles, Path.GetDirectoryName(config)!),
                    Directory = Path.GetDirectoryName(config)!
                };
                string? cache = IniDocument.Read(config).Get("GENERAL", "IntermediateCachePath");
                game.Build = cache != null &&
                    cache.Contains(paths.AddonBinaries, StringComparison.OrdinalIgnoreCase) ?
                    RuntimeBuild.FullAddon :
                    RuntimeBuild.Standard;
                state.Games.Add(game);
                string preset = Path.Combine(game.Directory, "ReShadePreset.ini");
                if (File.Exists(preset))
                {
                    transaction.Capture(preset);
                }
            }
        }

        return state;
    }

    private static IEnumerable<string> ProfileConfigurations(string root)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Legacy profile directory is a reparse point: " + root);
        }

        foreach (string file in Directory.EnumerateFiles(root))
        {
            if (!Path.GetFileName(file).Equals("ReShade.ini", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (FileTransaction.Link(file) != null)
            {
                throw new IOException("Resolve this legacy configuration link before importing: " + file);
            }

            yield return file;
        }

        foreach (string child in Directory.EnumerateDirectories(root))
        {
            foreach (string file in ProfileConfigurations(child))
            {
                yield return file;
            }
        }
    }

    public static void Relocate(
        ManagerState state,
        string newRoot,
        FileTransaction transaction,
        CancellationToken cancellationToken)
    {
        ManagerState previous = state.Clone();
        var destination = CentralPaths.At(newRoot);
        (string First, string Second)[] moves = previous.Paths.Directories().Zip(destination.Directories()).ToArray();
        // Destinations must be independent, empty trees; keep sources intact after a verified copy.
        foreach ((string? source, string? target) in moves)
        {
            if (moves.Any(pair => FileTransaction.Same(pair.First, target) ||
                target.StartsWith(pair.First.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ||
                pair.First.StartsWith(target.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
            {
                throw new IOException("The new central folder overlaps the current one. Open Runtime & storage > " +
                    "Relocate central folder and choose an empty folder outside the current central folder.");
            }

            if (Directory.Exists(target) && ShaderService.EnumerateFiles(target).Any())
            {
                throw new IOException($"The relocation destination is not empty: {target}. " +
                    "Open Runtime & storage > Relocate central folder and choose a different, empty folder.");
            }

            FileTransaction.CheckParents(Path.Combine(target, "check"));
        }

        foreach ((string? source, string? target) in moves)
        {
            if (!Directory.Exists(source))
            {
                continue;
            }

            foreach (string file in RelocationFiles(source, previous.Games.SelectMany(game => game.Links).ToList()))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string output = FileTransaction.Contained(target, Path.GetRelativePath(source, file));
                transaction.Copy(file, output);
                if (FileTransaction.Hash(file) != FileTransaction.Hash(output))
                {
                    throw new IOException("Relocation hash verification failed.");
                }
            }
        }

        foreach (GameProfile game in state.Games)
        {
            string oldDirectory = game.Directory;
            game.Directory = FileTransaction.Contained(
                destination.Profiles,
                Path.GetRelativePath(previous.Paths.Profiles, game.Directory));
            game.Links = game.Links.Select(link =>
            {
                bool insideProfile = link.Path.StartsWith(
                    oldDirectory.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
                if (!insideProfile)
                {
                    return link;
                }

                string relativePath = Path.GetRelativePath(oldDirectory, link.Path);
                string relocatedPath = FileTransaction.Contained(game.Directory, relativePath);
                return new OwnedLink(relocatedPath, link.Target);
            }).ToList();
        }

        state.Paths = destination;
        foreach (GameProfile game in state.Games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (game.Executable != null)
            {
                DeploymentService.Install(state, game, transaction, previous);
            }
            else
            {
                ConfigurationService.Update(state, game, transaction, previous);
            }
        }
    }

    private static IEnumerable<string> RelocationFiles(string root, List<OwnedLink> owned)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Relocation cannot traverse reparse-point directories.");
        }

        foreach (string file in Directory.EnumerateFiles(root))
        {
            if (FileTransaction.Link(file) != null)
            {
                if (!owned.Any(link => FileTransaction.Same(link.Path, file) && DeploymentService.IsOwnedLink(link)))
                {
                    throw new IOException("Unmanaged symlink in central storage: " + file);
                }

                continue; // Recreate recorded links at their new locations after copying.
            }

            yield return file;
        }

        foreach (string child in Directory.EnumerateDirectories(root))
        {
            foreach (string file in RelocationFiles(child, owned))
            {
                yield return file;
            }
        }
    }
}
