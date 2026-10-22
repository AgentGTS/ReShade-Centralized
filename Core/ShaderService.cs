using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ReShadeCentralized;

public sealed class ShaderPackage
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Repository { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public bool Required { get; init; }

    public bool Enabled { get; init; }

    public string[] DeniedEffects { get; init; } = [];
    public bool Extra { get; init; }

    public override string ToString() => Name + (Required ? " (required)" : "");
}

public sealed class ShaderService(Downloads downloads, StateStore store)
{
    public const string CatalogUrl =
        "https://raw.githubusercontent.com/crosire/reshade-shaders/list/EffectPackages.ini";

    public static string Resource(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        string resourceName = assembly.GetManifestResourceNames().Single(value => value.EndsWith("." + name));
        using Stream stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static string CanonicalRepository(string url) =>
        url.TrimEnd('/').ToLowerInvariant().Replace("radegast-ffxiv/reshade-shaders", "radegast-ffxiv/warp-fx")
        .Replace("github.com/originalnicoder/", "github.com/originalnicodr/");

    public static string PackageId(string repository)
    {
        string canonical = CanonicalRepository(repository);
        string slug = Regex.Replace(new Uri(canonical).AbsolutePath.Trim('/'), "[^a-z0-9]+", "-");
        string repositoryHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return $"{slug}-{repositoryHash[..8].ToLowerInvariant()}";
    }

    public static List<ShaderPackage> Parse(string text, bool extra = false)
    {
        var document = new IniDocument(text);
        var packages = new List<ShaderPackage>();
        foreach (string section in document.Sections)
        {
            string repository = document.Get(section, "RepositoryUrl") ??
                throw new InvalidDataException("Package repository URL is missing.");
            Downloads.Url(repository);
            string download = document.Get(section, "DownloadUrl") ??
                throw new InvalidDataException("Package download URL is missing.");
            Downloads.Url(download);
            packages.Add(new()
            {
                Id = PackageId(repository),
                Repository = repository,
                DownloadUrl = download,
                Name = document.Get(section, "PackageName") ?? repository,
                Description = document.Get(section, "PackageDescription") ?? "",
                Required = document.Get(section, "Required") == "1",
                Enabled = document.Get(section, "Enabled") == "1",
                DeniedEffects = IniDocument.Split(document.Get(section, "DenyEffectFiles")),
                Extra = extra
            });
        }

        if (packages.Count == 0)
        {
            throw new InvalidDataException("Shader catalog has no packages.");
        }

        return packages;
    }

    public async Task<List<ShaderPackage>> CatalogAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        string cache = Path.Combine(store.DirectoryPath, "CatalogCache.ini");
        List<ShaderPackage> official;
        try
        {
            string text = await downloads.TextAsync(CatalogUrl, cancellationToken);
            official = Parse(text);
            if (!official.Any(package => package.Required))
            {
                throw new InvalidDataException("Catalog is missing required standard effects.");
            }

            File.WriteAllText(cache + ".tmp", text);
            File.Move(cache + ".tmp", cache, true);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException ||
        (e is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            progress?.Report("Online catalog unavailable; using the cached/bundled catalog. " + e.Message);
            try
            {
                official = Parse(File.Exists(cache) ? File.ReadAllText(cache) : Resource("EffectPackages.ini"));
            }
            catch (Exception error) when (error is IOException or InvalidDataException)
            {
                official = Parse(Resource("EffectPackages.ini"));
            }
        }

        return official.Concat(Parse(Resource("ExtraPackages.ini"), true))
            .DistinctBy(package => CanonicalRepository(package.Repository))
            .ToList();
    }

    public static IReadOnlyList<ShaderPackage> WithDependencies(
        IEnumerable<ShaderPackage> selected,
        IReadOnlyList<ShaderPackage> catalog)
    {
        var result = selected.Concat(catalog.Where(package => package.Required)).DistinctBy(package => package.Id)
            .ToList();
        // The upstream catalog describes this dependency but has no structured dependency field.
        if (result.Any(package => CanonicalRepository(package.Repository).EndsWith("/yplebedev/bfbfx")))
        {
            ShaderPackage framework = catalog.SingleOrDefault(package =>
                CanonicalRepository(package.Repository).EndsWith("/zenteon/zenteonfx")) ??
                throw new InvalidDataException("BFBFX requires ZenteonFX, which is missing from the catalog.");
            if (!result.Any(package => package.Id == framework.Id))
            {
                result.Add(framework);
            }
        }

        return result.OrderByDescending(package => package.Required)
            .ThenBy(
            package => package.Id,
            StringComparer.Ordinal)
            .ToList();
    }

    public static string PackageDirectory(string root, string id) => FileTransaction.Contained(root, id);

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
                if (!package.Files.TryGetValue(key, out string? hash) || FileTransaction.Hash(path) != hash)
                {
                    throw new IOException("Local changes preserved; package update skipped: " + path);
                }
            }
        }

        if (package.Files.Keys.Any(value => !actual.Contains(value)))
        {
            throw new IOException("Local deletions preserved; package update skipped: " + package.Name);
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
            if (FileTransaction.Link(file) != null)
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
        if (previous == null &&
            ((Directory.Exists(shaderTarget) && EnumerateFiles(shaderTarget).Any()) ||
            (Directory.Exists(textureTarget) && EnumerateFiles(textureTarget).Any())))
        {
            throw new IOException("Unmanaged files occupy the package directory.");
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
                string destination = FileTransaction.Contained(target, relative);
                transaction.Copy(file, destination);
                installed.Files[kind + "/" + relative.Replace('\\', '/')] = FileTransaction.Hash(destination);
            }
        }

        if (previous != null)
        {
            foreach (string? file in previous.Files.Keys.Except(installed.Files.Keys, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                int slash = file.IndexOf('/');
                string root = file[..slash] == "shaders" ? shaderTarget : textureTarget;
                transaction.Delete(FileTransaction.Contained(root, file[(slash + 1)..]));
            }
        }

        state.Packages.RemoveAll(packageItem => packageItem.Id == package.Id);
        state.Packages.Add(installed);
        return installed;
    }

    public async Task<string> StageAsync(
        ShaderPackage package,
        string staging,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(staging);
        string zip = Path.Combine(staging, "package.zip");
        await downloads.FileAsync(package.DownloadUrl, zip, progress, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        string extracted = Path.Combine(staging, "extracted");
        SafeArchive.Extract(zip, extracted, cancellationToken);
        return extracted;
    }

    public static List<string> DuplicateEffects(ManagerState state)
    {
        var managedEffects = state.Packages.SelectMany(package => package.Files.Keys
            .Where(path => path.StartsWith("shaders/") && path.EndsWith(".fx", StringComparison.OrdinalIgnoreCase))
            .Select(path => (Name: Path.GetFileName(path), Package: package.Name)));
        IEnumerable<(string Name, string Package)> legacyEffects = [];
        if (Directory.Exists(state.Paths.Shaders))
        {
            legacyEffects = Directory.EnumerateFiles(state.Paths.Shaders, "*.fx", SearchOption.TopDirectoryOnly)
                .Select(path => (Name: Path.GetFileName(path), Package: "Legacy / custom flat folder"));
        }

        return managedEffects.Concat(legacyEffects).GroupBy(effect => effect.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key}: {string.Join(", ", group.Select(effect => effect.Package))}")
            .ToList();
    }
}
