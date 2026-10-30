namespace ReShadeCentralized;

public static class ShaderFiles
{
    public static string PackageDirectory(string root, string id)
    {
        if (string.IsNullOrWhiteSpace(id) || Path.GetFileName(id) != id)
        {
            throw new InvalidDataException("A shader package ID must be a single folder name.");
        }
        return LocalFiles.Contained(root, id);
    }

    public static bool IsPackageComplete(InstalledPackage package, CentralPaths paths)
    {
        if (package.Files.Count == 0)
        {
            return false;
        }
        try
        {
            return package.Files.Keys.All(key =>
            {
                string[] parts = key.Split('/', 2);
                if (parts.Length != 2 || parts[0] is not ("shaders" or "textures"))
                {
                    return false;
                }
                string root = parts[0] == "shaders" ? paths.Shaders : paths.Textures;
                string file = LocalFiles.Contained(PackageDirectory(root, package.Id), parts[1]);
                LocalFiles.CheckParents(file);
                return LocalFiles.Link(file) == null && File.Exists(file);
            });
        }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidDataException)
        {
            return false;
        }
    }

    public static void CheckUnmodified(InstalledPackage package, CentralPaths paths)
    {
        var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string? kind, string? root) in new[]
        {
            ("shaders", paths.Shaders),
            ("textures", paths.Textures)
        })
        {
            string directory = PackageDirectory(root, package.Id);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (string path in EnumerateFiles(directory))
            {
                string key = kind + "/" + Path.GetRelativePath(directory, path).Replace('\\', '/');
                actual.Add(key);
                if (!package.Files.TryGetValue(key, out string? hash) || LocalFiles.Hash(path) != hash)
                {
                    throw new IOException($"Local changes were found in {path}. Restore the original files, " +
                        "or use Shader packages > Purge shader folder to reinstall all central shader packages.");
                }
            }
        }

        if (package.Files.Keys.Any(value => !actual.Contains(value)))
        {
            throw new IOException($"Files are missing from {package.Name}. Restore the original files, " +
                "or use Shader packages > Purge shader folder, then Apply selection.");
        }
    }

    public static IEnumerable<string> EnumerateFiles(string root)
    {
        // Do not traverse junctions, including package or development folders.
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Reparse-point directory is not supported: " + root);
        }

        foreach (string file in Directory.EnumerateFiles(root))
        {
            if (LocalFiles.Link(file) != null)
            {
                throw new IOException("Unexpected file link: " + file);
            }

            yield return file;
        }

        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            foreach (string file in EnumerateFiles(directory))
            {
                yield return file;
            }
        }
    }

    private static string? FindTree(string extracted, string name, string[] extensions)
    {
        string[] dirs = Directory.GetDirectories(extracted, name, SearchOption.AllDirectories)
            .OrderBy(value => value.Length)
            .ToArray();
        if (dirs.Length > 0)
        {
            return dirs[0];
        }

        string[] candidates = EnumerateFiles(extracted)
            .Where(value => extensions.Contains(Path.GetExtension(value)
                .ToLowerInvariant()))
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        string common = Path.GetDirectoryName(candidates[0])!;
        while (!candidates.All(value => value.StartsWith(
                common.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)))
        {
            common = Path.GetDirectoryName(common)!;
        }

        return common;
    }

    public static InstalledPackage InstallExtracted(
        ShaderPackage package,
        string extracted,
        ManagerState state,
        FileTransaction transaction,
        CancellationToken cancellationToken)
    {
        InstalledPackage? previous = state.Packages.SingleOrDefault(packageItem => packageItem.Id == package.Id);
        if (previous != null)
        {
            CheckUnmodified(previous, state.Paths);
        }

        string shaderTarget = PackageDirectory(state.Paths.Shaders, package.Id);
        string textureTarget = PackageDirectory(state.Paths.Textures, package.Id);
        if (previous == null)
        {
            string[] occupied = new[] { shaderTarget, textureTarget }
                .Where(path => Directory.Exists(path) && EnumerateFiles(path).Any()).ToArray();
            if (occupied.Length > 0)
            {
                throw new IOException("Unmanaged files occupy the package directory: " + string.Join(", ", occupied) +
                    ". Use Shader packages > Purge shader folder, then Apply selection " +
                    "to reinstall all central shader packages.");
            }
        }

        string[] shaderTypes = [".fx", ".fxh", ".h", ".hlsl", ".cfg", ".txt", ".json"];
        string[] textureTypes = [".png", ".dds", ".bmp", ".jpg", ".jpeg", ".tga", ".exr", ".webp", ".cube"];
        string? shaderSource = FindTree(extracted, "Shaders", [".fx", ".fxh"]);
        string? textureSource = FindTree(extracted, "Textures", textureTypes);
        if (shaderSource == null && textureSource == null)
        {
            throw new InvalidDataException("No shaders or textures were found in " + package.Name);
        }

        var installed = new InstalledPackage
        {
            Id = package.Id,
            Name = package.Name,
            Repository = package.Repository,
            DownloadUrl = package.DownloadUrl,
            Updated = DateTimeOffset.UtcNow
        };
        foreach ((string? kind, string? source, string? target, string[]? extensions) in new[]
        {
            ("shaders", shaderSource, shaderTarget, shaderTypes),
            ("textures", textureSource, textureTarget, textureTypes)
        })
        {
            if (source == null)
            {
                continue;
            }

            foreach (string file in EnumerateFiles(source))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!extensions.Contains(Path.GetExtension(file).ToLowerInvariant()) ||
                    package.DeniedEffects.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                string relative = Path.GetRelativePath(source, file);
                string destination = LocalFiles.Contained(target, relative);
                transaction.Copy(file, destination);
                installed.Files[kind + "/" + relative.Replace('\\', '/')] = LocalFiles.Hash(destination);
            }
        }

        foreach (string notice in EnumerateFiles(extracted)
            .Where(file => IsNotice(Path.GetRelativePath(extracted, file))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = "_notices/" + Path.GetRelativePath(extracted, notice).Replace('\\', '/');
            string destination = LocalFiles.Contained(shaderTarget, relative);
            transaction.Copy(notice, destination);
            installed.Files["shaders/" + relative] = LocalFiles.Hash(destination);
        }

        if (previous != null)
        {
            foreach (string file in previous.Files.Keys.Except(installed.Files.Keys, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                int slash = file.IndexOf('/');
                string root = file[..slash] == "shaders" ? shaderTarget : textureTarget;
                transaction.Delete(LocalFiles.Contained(root, file[(slash + 1)..]));
            }
        }

        state.Packages.RemoveAll(packageItem => packageItem.Id == package.Id);
        state.Packages.Add(installed);
        return installed;
    }

    private static bool IsNotice(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not ("" or ".txt" or ".md" or ".rst" or ".html" or ".spdx"))
        {
            return false;
        }
        string name = Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
        return new[]
            { "LICENSE", "LICENCE", "COPYING", "COPYRIGHT", "NOTICE", "AUTHORS", "CREDITS", "UNLICENSE", "README" }
            .Any(prefix => name == prefix || name.StartsWith(prefix + "-") || name.StartsWith(prefix + "_")) ||
            path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part.Equals("licenses", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals("licences", StringComparison.OrdinalIgnoreCase));
    }

    public static List<string> DuplicateEffects(ManagerState state)
    {
        var managedEffects = state.Packages.SelectMany(package => package.Files.Keys
            .Where(path => path.StartsWith("shaders/") && path.EndsWith(".fx", StringComparison.OrdinalIgnoreCase))
            .Select(path => (Name: Path.GetFileName(path), Package: package.Name)));
        return managedEffects.GroupBy(effect => effect.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key}: {string.Join(", ", group.Select(effect => effect.Package))}")
            .ToList();
    }
}
