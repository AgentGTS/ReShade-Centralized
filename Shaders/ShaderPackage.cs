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
    public override string ToString() => Name + (Required ? " (required)" : "");
}
