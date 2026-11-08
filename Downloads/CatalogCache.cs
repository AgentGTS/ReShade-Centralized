namespace ReShadeCentralized;

internal static class CatalogCache
{
    private const long MaximumExistingCacheBytes = 16 * 1024 * 1024;

    public static string? Read(string path, IProgress<string>? progress = null)
    {
        try
        {
            CheckPath(path);
            if (File.Exists(path) && new FileInfo(path).Length > MaximumExistingCacheBytes)
            {
                throw new IOException("The catalog cache is too large to read safely.");
            }

            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            progress?.Report("Cannot read catalog cache: " + error.Message);
            return null;
        }
    }

    public static async Task<T> RefreshAsync<T>(
        Downloads downloads,
        string url,
        string path,
        Func<string, T> parse,
        Func<T> fallback,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            progress?.Report("Downloading " + Path.GetFileName(new Uri(url).LocalPath) + " from upstream...");
            return await DownloadAsync(downloads, url, path, parse, progress, cancellationToken);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or
            UnauthorizedAccessException
            || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            progress?.Report("Catalog download unavailable; using cached or offline data. " + error.Message);
            return fallback();
        }
    }

    private static async Task<T> DownloadAsync<T>(
        Downloads downloads,
        string url,
        string path,
        Func<string, T> parse,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        string text = await downloads.TextAsync(url, timeout.Token);
        T catalog = parse(text);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool created = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckPath(path);
            byte[]? previous = null;
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > MaximumExistingCacheBytes)
                {
                    progress?.Report("Preserved an oversized catalog cache; refreshed data is available for this session: " + path);
                    return catalog;
                }

                previous = await File.ReadAllBytesAsync(path, cancellationToken);
                try
                {
                    using var reader = new StreamReader(new MemoryStream(previous));
                    _ = parse(await reader.ReadToEndAsync(cancellationToken));
                }
                catch (Exception error) when (error is InvalidDataException or ArgumentException or FormatException)
                {
                    progress?.Report("Preserved an unrecognized catalog cache; refreshed data is available for this session: " + path);
                    return catalog;
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                created = true;
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(text.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            CheckPath(path);
            if (previous != null && (!File.Exists(path)
                || new FileInfo(path).Length > MaximumExistingCacheBytes
                || !Enumerable.SequenceEqual(previous, await File.ReadAllBytesAsync(path, cancellationToken))))
            {
                progress?.Report("Preserved a catalog cache that changed during refresh: " + path);
                return catalog;
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: previous != null);
        }
        finally
        {
            if (created && File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        return catalog;
    }

    private static void CheckPath(string path)
    {
        LocalFiles.CheckParents(path);
        if (LocalFiles.Link(path) != null || File.Exists(path)
            && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("A catalog cache cannot be a reparse point.");
        }
    }
}
