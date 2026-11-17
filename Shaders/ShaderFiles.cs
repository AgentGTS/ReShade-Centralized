namespace ReShadeCentralized;

public static class ShaderFiles {
    private static readonly string[] shaderSources = [".fx", ".fxh", ".h", ".hlsl"];
    private static readonly string[] shaderTypes = [.. shaderSources, ".cfg", ".txt", ".json"];
    private static readonly string[] textureTypes =
        [".png", ".dds", ".bmp", ".jpg", ".jpeg", ".tga", ".exr", ".webp", ".cube"];

    public static string PackageDirectory(string root, string id) {
        if (string.IsNullOrWhiteSpace(id) || Path.GetFileName(id) != id)
            throw new InvalidDataException("A shader package ID must be a single folder name.");
        return LocalFiles.Contained(root, id);
    }

    public static bool IsPackageComplete(InstalledPackage package, CentralPaths paths) {
        if (!package.Files.Keys.Any(IsPayload))
            return false;
        try {
            return package.Files.Keys.All(key => {
                string[] parts = key.Split('/', 2);
                if (parts.Length != 2 || parts[0] is not ("shaders" or "textures"))
                    return false;
                string root = parts[0] == "shaders" ? paths.Shaders : paths.Textures;
                string file = LocalFiles.Contained(PackageDirectory(root, package.Id), parts[1]);
                LocalFiles.CheckParents(file);
                return LocalFiles.Link(file) == null && File.Exists(file);
            });
        } catch (Exception error) when (error is IOException or ArgumentException or InvalidDataException) {
            return false;
        }
    }

    public static void CheckUnmodified(
        InstalledPackage package,
        CentralPaths paths,
        CancellationToken cancellationToken = default) {
        var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string? kind, string? root) in new[] {
            ("shaders", paths.Shaders),
            ("textures", paths.Textures)
        }) {
            string directory = PackageDirectory(root, package.Id);
            if (!Directory.Exists(directory))
                continue;

            foreach (string path in EnumerateFiles(directory)) {
                cancellationToken.ThrowIfCancellationRequested();
                string key = kind + "/" + Path.GetRelativePath(directory, path).Replace('\\', '/');
                actual.Add(key);
                if (!package.Files.TryGetValue(key, out string? hash) || LocalFiles.Hash(path, cancellationToken) != hash)
                {
                    throw new IOException($"Local changes were found in {path}. Restore the original files, " +
                        "or use Shader packages > Purge shader folder to reinstall all central shader packages.");
                }
            }
        }

        if (package.Files.Keys.Any(value => !actual.Contains(value))) {
            throw new IOException($"Files are missing from {package.Name}. Restore the original files, " +
                "or use Shader packages > Purge shader folder, then Apply selection.");
        }
    }

    public static IEnumerable<string> EnumerateFiles(string root) {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Reparse-point directory is not supported: " + root);

        foreach (string file in Directory.EnumerateFiles(root)) {
            if (LocalFiles.Link(file) != null)
                throw new IOException("Unexpected file link: " + file);

            yield return file;
        }

        foreach (string file in Directory.EnumerateDirectories(root).SelectMany(EnumerateFiles)) {
            yield return file;
        }
    }

    private static string? FindTree(string extracted, string name, string[] extensions) {
        string? nearest = Directory.EnumerateDirectories(extracted, name, SearchOption.AllDirectories)
            .MinBy(directory => directory.Length);
        if (nearest != null)
            return nearest;

        string[] candidates = EnumerateFiles(extracted)
            .Where(value => extensions.Contains(Path.GetExtension(value)
                .ToLowerInvariant()))
            .ToArray();
        if (candidates.Length == 0)
            return null;

        string common = Path.GetDirectoryName(candidates[0])!;
        while (!candidates.All(value => value.StartsWith(
                common.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))) {
            common = Path.GetDirectoryName(common)!;
        }

        return common;
    }

    public static InstalledPackage InstallExtracted(
        ShaderPackage package,
        string extracted,
        ManagerState state,
        FileTransaction transaction,
        CancellationToken cancellationToken) {
        InstalledPackage? previous = state.Packages.SingleOrDefault(packageItem => packageItem.Id == package.Id);
        if (previous != null)
            CheckUnmodified(previous, state.Paths, cancellationToken);

        string shaderTarget = PackageDirectory(state.Paths.Shaders, package.Id);
        string textureTarget = PackageDirectory(state.Paths.Textures, package.Id);
        if (previous == null) {
            string[] occupied = new[] { shaderTarget, textureTarget }
                .Where(path => Directory.Exists(path) && EnumerateFiles(path).Any()).ToArray();
            if (occupied.Length > 0) {
                throw new IOException("Unmanaged files occupy the package directory: " + string.Join(", ", occupied) +
                    ". Use Shader packages > Purge shader folder, then Apply selection " +
                    "to reinstall all central shader packages.");
            }
        }

        string? shaderSource = FindTree(extracted, "Shaders", [".fx", ".fxh"]);
        string? textureSource = FindTree(extracted, "Textures", textureTypes);
        if (shaderSource == null && textureSource == null)
            throw new InvalidDataException("No shaders or textures were found in " + package.Name);

        var installed = new InstalledPackage {
            Id = package.Id,
            Name = package.Name,
            Repository = package.Repository,
            DownloadUrl = package.DownloadUrl,
            Updated = DateTimeOffset.UtcNow
        };
        foreach ((string? kind, string? source, string? target, string[]? extensions) in new[] {
            ("shaders", shaderSource, shaderTarget, shaderTypes),
            ("textures", textureSource, textureTarget, textureTypes)
        }) {
            if (source == null)
                continue;

            foreach (string file in EnumerateFiles(source)) {
                cancellationToken.ThrowIfCancellationRequested();
                if (!extensions.Contains(Path.GetExtension(file).ToLowerInvariant())
                    || package.DeniedEffects.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase))
                    continue;

                string relative = Path.GetRelativePath(source, file);
                string destination = LocalFiles.Contained(target, relative);
                string key = kind + "/" + relative.Replace('\\', '/');
                CopyPackageFile(file, destination, key, previous, installed, transaction, cancellationToken);
            }
        }

        if (!installed.Files.Keys.Any(IsPayload))
            throw new InvalidDataException("No supported shaders or textures remain in " + package.Name);

        foreach (string notice in EnumerateFiles(extracted)
            .Where(file => IsNotice(Path.GetRelativePath(extracted, file)))) {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = "_notices/" + Path.GetRelativePath(extracted, notice).Replace('\\', '/');
            string destination = LocalFiles.Contained(shaderTarget, relative);
            CopyPackageFile(notice, destination, "shaders/" + relative, previous, installed, transaction, cancellationToken);
        }

        if (previous != null) {
            foreach (string file in previous.Files.Keys.Except(installed.Files.Keys, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                int slash = file.IndexOf('/');
                string root = file[..slash] == "shaders" ? shaderTarget : textureTarget;
                string target = LocalFiles.Contained(root, file[(slash + 1)..]);
                if (!transaction.DeleteIfUnchanged(target, expectedHash: previous.Files[file]))
                    throw new IOException("A shader file changed while it was being updated: " + target);
            }
        }

        state.Packages.RemoveAll(packageItem => packageItem.Id == package.Id);
        state.Packages.Add(installed);
        return installed;
    }

    private static void CopyPackageFile(
        string source,
        string destination,
        string key,
        InstalledPackage? previous,
        InstalledPackage installed,
        FileTransaction transaction,
        CancellationToken cancellationToken) {
        if (installed.Files.ContainsKey(key))
            throw new InvalidDataException("Multiple package files target the same shader path: " + destination);

        string? expected = previous != null && previous.Files.TryGetValue(key, out string? hash) ? hash : null;
        string sourceHash = LocalFiles.Hash(source, cancellationToken);
        if (!transaction.CopyIfUnchanged(source, destination, expected, cancellationToken)
            || !string.Equals(LocalFiles.Hash(destination, cancellationToken), sourceHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("A shader file changed while it was being updated: " + destination);

        installed.Files[key] = sourceHash;
    }

    private static bool IsPayload(string key) {
        string extension = Path.GetExtension(key);
        return key.StartsWith("textures/", StringComparison.OrdinalIgnoreCase)
            && textureTypes.Contains(extension, StringComparer.OrdinalIgnoreCase)
            || key.StartsWith("shaders/", StringComparison.OrdinalIgnoreCase)
            && !key.StartsWith("shaders/_notices/", StringComparison.OrdinalIgnoreCase)
            && shaderSources.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsNotice(string path) {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not ("" or ".txt" or ".md" or ".rst" or ".html" or ".spdx"))
            return false;
        string name = Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
        return new[]
            { "LICENSE", "LICENCE", "COPYING", "COPYRIGHT", "NOTICE", "AUTHORS", "CREDITS", "UNLICENSE", "README" }
            .Any(prefix => name == prefix || name.StartsWith(prefix + "-") || name.StartsWith(prefix + "_"))
            || path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part.Equals("licenses", StringComparison.OrdinalIgnoreCase)
                    || part.Equals("licences", StringComparison.OrdinalIgnoreCase));
    }

    public static List<string> DuplicateEffects(ManagerState state) {
        var managedEffects = state.Packages.SelectMany(package => package.Files.Keys
            .Where(path => path.StartsWith("shaders/") && path.EndsWith(".fx", StringComparison.OrdinalIgnoreCase))
            .Select(path => (Name: Path.GetFileName(path), Package: package.Name)));
        return managedEffects.GroupBy(effect => effect.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key}: {string.Join(", ", group.Select(effect => effect.Package))}")
            .ToList();
    }
}

public sealed class InstalledPackage {
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Repository { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public DateTimeOffset Updated { get; set; }

    public Dictionary<string, string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public InstalledPackage Clone() => new() {
        Id = Id,
        Name = Name,
        Repository = Repository,
        DownloadUrl = DownloadUrl,
        Updated = Updated,
        Files = new(Files, StringComparer.OrdinalIgnoreCase)
    };
}
