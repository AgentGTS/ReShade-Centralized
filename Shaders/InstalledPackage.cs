namespace ReShadeCentralized;

public sealed class PackageUpdateReport
{
    public int InstalledCount { get; internal set; }
    public int RemovedCount { get; internal set; }
    public int FailedCount { get; internal set; }
    public List<string> Messages { get; } = [];
    public string Summary =>
        $"Shader packages: {InstalledCount} installed/updated, {RemovedCount} removed, {FailedCount} failed.";
}

public sealed record ShaderSelectionPlan(
    IReadOnlyList<ShaderPackage> Selected,
    IReadOnlyList<InstalledPackage> Removals,
    string Fingerprint);

public sealed class InstalledPackage
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Repository { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public DateTimeOffset Updated { get; set; }

    public Dictionary<string, string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public InstalledPackage Clone() => new()
    {
        Id = Id,
        Name = Name,
        Repository = Repository,
        DownloadUrl = DownloadUrl,
        Updated = Updated,
        Files = new(Files, StringComparer.OrdinalIgnoreCase)
    };
}
