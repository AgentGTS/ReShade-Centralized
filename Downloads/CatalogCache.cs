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

    public static async Task<string> DownloadAsync(
        Downloads downloads, string url, string path, Action<string> validate,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        string text = await downloads.TextAsync(url, timeout.Token);
        validate(text);
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
        return text;
    }
}
