namespace ReShadeCentralized;

internal static class CatalogCache
{
    public static string? Read(string path, IProgress<string>? progress = null)
    {
        try
        {
            LocalFiles.CheckParents(path);
            if (LocalFiles.Link(path) != null)
            {
                throw new IOException("A catalog cache cannot be a symbolic link.");
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
        Downloads downloads, string url, string path, Func<string, T> parse, Func<T> fallback,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        try
        {
            progress?.Report("Downloading " + Path.GetFileName(new Uri(url).LocalPath) + " from upstream...");
            return await DownloadAsync(downloads, url, path, parse, cancellationToken);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or
            UnauthorizedAccessException ||
            (error is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            progress?.Report("Catalog download unavailable; using cached or offline data. " + error.Message);
            return fallback();
        }
    }

    private static async Task<T> DownloadAsync<T>(
        Downloads downloads, string url, string path, Func<string, T> parse,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        string text = await downloads.TextAsync(url, timeout.Token);
        T catalog = parse(text);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            LocalFiles.CheckParents(path);
            if (LocalFiles.Link(path) != null)
            {
                throw new IOException("A catalog cache cannot replace a symbolic link.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(temporary, text, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
        return catalog;
    }
}
