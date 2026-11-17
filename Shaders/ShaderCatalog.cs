using System.Text.RegularExpressions;

namespace ReShadeCentralized;

public sealed class ShaderCatalog(Downloads downloads, StateStore store) {
    public const string CatalogUrl =
        "https://raw.githubusercontent.com/crosire/reshade-shaders/list/EffectPackages.ini";
    public const string CacheName = "CatalogCache.ini";

    public static string CanonicalRepository(string url) =>
        url.TrimEnd('/').ToLowerInvariant().Replace("radegast-ffxiv/reshade-shaders", "radegast-ffxiv/warp-fx")
        .Replace("github.com/originalnicoder/", "github.com/originalnicodr/");

    public static string PackageId(string repository) {
        string canonical = CanonicalRepository(repository);
        string slug = Regex.Replace(new Uri(canonical).AbsolutePath.Trim('/'), "[^a-z0-9]+", "-");
        string repositoryHash = LocalFiles.HashText(canonical);
        return $"{slug}-{repositoryHash[..8].ToLowerInvariant()}";
    }

    public static List<ShaderPackage> Parse(string text) {
        var document = new IniDocument(text);
        var packages = new List<ShaderPackage>();
        foreach (string section in document.Sections) {
            string repository = document.Get(section, "RepositoryUrl") ??
                throw new InvalidDataException("Package repository URL is missing.");
            Downloads.Url(repository);
            string download = document.Get(section, "DownloadUrl") ??
                throw new InvalidDataException("Package download URL is missing.");
            Downloads.Url(download);
            packages.Add(new() {
                Id = PackageId(repository),
                Repository = repository,
                DownloadUrl = download,
                Name = document.Get(section, "PackageName") ?? repository,
                Description = document.Get(section, "PackageDescription") ?? "",
                Required = document.Get(section, "Required") == "1",
                Enabled = document.Get(section, "Enabled") == "1",
                EffectFiles = IniDocument.Split(document.Get(section, "EffectFiles")),
                DeniedEffects = IniDocument.Split(document.Get(section, "DenyEffectFiles"))
            });
        }

        if (packages.Count == 0)
            throw new InvalidDataException("Shader catalog has no packages.");

        return packages;
    }

    public Task<List<ShaderPackage>> CatalogAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        List<ShaderPackage>? cached = ReadCachedCatalog(progress);
        if (cached != null)
            return Task.FromResult(cached);

        return RefreshAsync(() => {
            progress?.Report("No shader catalog is available. Open Shader packages > Refresh catalog to retry.");
            return [];
        }, progress, cancellationToken);
    }

    public Task<List<ShaderPackage>> RefreshAsync(IProgress<string>? progress, CancellationToken cancellationToken) =>
        RefreshAsync(null, progress, cancellationToken);

    private Task<List<ShaderPackage>> RefreshAsync(
        Func<List<ShaderPackage>>? fallback,
        IProgress<string>? progress,
        CancellationToken cancellationToken) =>
        CatalogCache.RefreshAsync(downloads, CatalogUrl, Path.Combine(store.DirectoryPath, CacheName),
            ParseRequired, fallback, progress, cancellationToken);

    private List<ShaderPackage>? ReadCachedCatalog(IProgress<string>? progress) {
        if (!store.IsSelected
            || CatalogCache.Read(Path.Combine(store.DirectoryPath, CacheName), progress) is not string cached)
            return null;

        try {
            return ParseRequired(cached);
        } catch (Exception error) when (error is InvalidDataException or ArgumentException or FormatException) {
            progress?.Report("Ignoring an invalid shader catalog cache: " + error.Message);
            return null;
        }
    }

    private static List<ShaderPackage> ParseRequired(string text) {
        List<ShaderPackage> packages = Parse(text)
            .DistinctBy(package => CanonicalRepository(package.Repository))
            .ToList();
        return packages.Any(package => package.Required) ? packages :
            throw new InvalidDataException("Catalog is missing required standard effects.");
    }
}

public sealed class ShaderPackage {
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Repository { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public bool Required { get; init; }

    public bool Enabled { get; init; }
    internal bool InstalledOnly { get; init; }

    public string[] EffectFiles { get; init; } = [];
    public string[] DeniedEffects { get; init; } = [];
    public override string ToString() => Name + (Required ? " (required)" : "");
}

internal static class CatalogCache {
    private const long MaximumExistingCacheBytes = 16 * 1024 * 1024;

    public static string? Read(string path, IProgress<string>? progress = null) {
        try {
            CheckPath(path);
            if (File.Exists(path) && new FileInfo(path).Length > MaximumExistingCacheBytes)
                throw new IOException("The catalog cache is too large to read safely.");

            return File.Exists(path) ? File.ReadAllText(path) : null;
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
            progress?.Report("Cannot read catalog cache: " + error.Message);
            return null;
        }
    }

    public static async Task<T> RefreshAsync<T>(
        Downloads downloads,
        string url,
        string path,
        Func<string, T> parse,
        Func<T>? fallback,
        IProgress<string>? progress,
        CancellationToken cancellationToken) {
        try {
            progress?.Report("Downloading " + Path.GetFileName(new Uri(url).LocalPath) + " from upstream...");
            return await DownloadAsync(downloads, url, path, parse, progress, cancellationToken);
        } catch (Exception error) when (fallback != null
            && (error is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException
                    or ArgumentException or FormatException
                || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))) {
            progress?.Report("Catalog download unavailable. " + error.Message);
            return fallback();
        }
    }

    private static async Task<T> DownloadAsync<T>(
        Downloads downloads,
        string url,
        string path,
        Func<string, T> parse,
        IProgress<string>? progress,
        CancellationToken cancellationToken) {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        string text = await downloads.TextAsync(url, timeout.Token);
        T catalog = parse(text);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool created = false;

        try {
            cancellationToken.ThrowIfCancellationRequested();
            CheckPath(path);
            byte[]? previous = null;
            if (File.Exists(path)) {
                if (new FileInfo(path).Length > MaximumExistingCacheBytes) {
                    progress?.Report("Preserved an oversized catalog cache; refreshed data is available for this session: " + path);
                    return catalog;
                }

                previous = await File.ReadAllBytesAsync(path, cancellationToken);
                try {
                    using var reader = new StreamReader(new MemoryStream(previous));
                    _ = parse(await reader.ReadToEndAsync(cancellationToken));
                } catch (Exception error) when (error is InvalidDataException or ArgumentException or FormatException) {
                    progress?.Report("Preserved an unrecognized catalog cache; refreshed data is available for this session: " + path);
                    return catalog;
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous)) {
                created = true;
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(text.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            CheckPath(path);
            if (previous != null && (!File.Exists(path)
                || new FileInfo(path).Length > MaximumExistingCacheBytes
                || !Enumerable.SequenceEqual(previous, await File.ReadAllBytesAsync(path, cancellationToken)))) {
                progress?.Report("Preserved a catalog cache that changed during refresh: " + path);
                return catalog;
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: previous != null);
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
            progress?.Report("Could not save the shader catalog cache; refreshed data is available for this session. " +
                error.Message);
        } finally {
            if (created) {
                try {
                    CheckPath(temporary);
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                } catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
                    progress?.Report("Could not remove the temporary shader catalog cache: " + error.Message);
                }
            }
        }

        return catalog;
    }

    private static void CheckPath(string path) {
        LocalFiles.CheckParents(path);
        if (LocalFiles.Link(path) != null || File.Exists(path)
            && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("A catalog cache cannot be a reparse point.");
    }
}
