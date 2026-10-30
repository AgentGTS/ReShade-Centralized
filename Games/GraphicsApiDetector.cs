namespace ReShadeCentralized;

public static class GraphicsApiDetector
{
    public static RenderApi? Detect(string path, CancellationToken cancellationToken = default)
    {
        RenderApi? detected = ExecutableImports.Detect(path, cancellationToken, out string? executable);
        return detected ?? (executable == null ? null : CompatibilityCatalog.Default.Find(executable)?.Api);
    }
}
