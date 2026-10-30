namespace ReShadeCentralized;

internal sealed class StagingDirectory : IDisposable
{
    public string Path { get; }

    public StagingDirectory(StateStore store)
    {
        Path = System.IO.Path.Combine(store.Staging, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path);
        }
        catch (IOException error)
        {
            throw new IOException($"Cannot prepare the download folder {store.Staging}. " +
                "Make sure this path is a writable folder rather than a file, then retry the download.", error);
        }
    }

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
