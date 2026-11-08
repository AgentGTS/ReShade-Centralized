namespace ReShadeCentralized;

internal sealed class StagingDirectory : IDisposable
{
    private readonly Action<string> reportWarning;
    private readonly string marker;
    public string Path { get; }

    public StagingDirectory(StateStore store, Action<string> reportWarning)
    {
        this.reportWarning = reportWarning;
        Path = System.IO.Path.Combine(store.Staging, Guid.NewGuid().ToString("N"));
        marker = System.IO.Path.Combine(Path, ".reshade-centralized-" + Guid.NewGuid().ToString("N"));

        try
        {
            LocalFiles.CheckParents(System.IO.Path.Combine(Path, "check"));
            if (Directory.Exists(Path) || LocalFiles.Exists(Path))
            {
                throw new IOException("The temporary folder already exists.");
            }
            Directory.CreateDirectory(Path);
            LocalFiles.CheckParents(System.IO.Path.Combine(Path, "check"));
            using FileStream ownership = new(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (IOException error)
        {
            throw new IOException($"Cannot prepare the download folder {store.Staging}. " +
                "Make sure this path is a writable folder rather than a file, then retry the download.", error);
        }
    }

    public void Dispose()
    {
        try
        {
            LocalFiles.CheckParents(System.IO.Path.Combine(Path, "check"));
            if (!Directory.Exists(Path))
            {
                return;
            }
            if (LocalFiles.Link(marker) != null || !File.Exists(marker))
            {
                throw new IOException("The temporary folder ownership marker is missing or changed.");
            }
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            reportWarning("Could not safely remove temporary files: " + Path + ". " + error.Message);
        }
    }
}
