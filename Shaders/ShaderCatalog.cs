using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ReShadeCentralized;

public sealed class ShaderCatalog(Downloads downloads, StateStore store)
{
    public const string CatalogUrl =
        "https://raw.githubusercontent.com/crosire/reshade-shaders/list/EffectPackages.ini";
    public const string CacheName = "CatalogCache.ini";

    public bool HasCachedCatalog => store.IsSelected &&
        File.Exists(Path.Combine(store.DirectoryPath, CacheName));

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

    public static List<ShaderPackage> Parse(string text)
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
                DeniedEffects = IniDocument.Split(document.Get(section, "DenyEffectFiles"))
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
        string cache = Path.Combine(store.DirectoryPath, CacheName);
        List<ShaderPackage> catalog;
        try
        {
            progress?.Report("Downloading shader package information from upstream...");
            string text = await CatalogCache.DownloadAsync(downloads, CatalogUrl, cache, text =>
            {
                if (!Parse(text).Any(package => package.Required))
                {
                    throw new InvalidDataException("Catalog is missing required standard effects.");
                }
            }, cancellationToken);
            catalog = Combine(Parse(text));
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or
            UnauthorizedAccessException ||
            (error is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            progress?.Report("Shader catalog download unavailable; using the cache or minimal offline list. " +
                error.Message);
            catalog = CachedCatalog(progress);
        }

        return catalog;
    }

    public List<ShaderPackage> CachedCatalog(IProgress<string>? progress = null)
    {
        string? cached = store.IsSelected ?
            CatalogCache.Read(Path.Combine(store.DirectoryPath, CacheName), progress) : null;
        if (cached != null)
        {
            try
            {
                List<ShaderPackage> packages = Parse(cached);
                if (packages.Any(package => package.Required))
                {
                    return Combine(packages);
                }
            }
            catch (Exception error) when (error is InvalidDataException)
            {
                progress?.Report("Ignoring an invalid shader catalog cache: " + error.Message);
            }
        }

        const string standard = "https://github.com/crosire/reshade-shaders";
        return Combine([new ShaderPackage
        {
            Id = PackageId(standard + "/tree/slim"),
            Name = "Standard effects",
            Description = "Core shader utilities. Refresh catalog for the full upstream package list.",
            Repository = standard + "/tree/slim",
            DownloadUrl = standard + "/archive/slim.zip",
            Required = true,
            Enabled = true
        }]);
    }

    private static List<ShaderPackage> Combine(IEnumerable<ShaderPackage> official) =>
        official.Concat(Parse(Resource("ExtraPackages.ini")))
            .DistinctBy(package => CanonicalRepository(package.Repository)).ToList();

}
