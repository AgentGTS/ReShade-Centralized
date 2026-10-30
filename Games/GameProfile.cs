namespace ReShadeCentralized;

public enum RenderApi
{
    Direct3D9,
    DXGI,
    OpenGL,
    Vulkan
}

public enum CpuArchitecture
{
    X86,
    X64
}

public sealed class GameProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Executable { get; set; } = "";

    public RenderApi Api { get; set; }

    public RuntimeBuild Build { get; set; }

    public CpuArchitecture Architecture { get; set; }

    public string Directory { get; set; } = "";
    public List<OwnedLink> Links { get; set; } = [];

    public GameProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        Executable = Executable,
        Api = Api,
        Build = Build,
        Architecture = Architecture,
        Directory = Directory,
        Links = [.. Links]
    };
}

public sealed record OwnedLink(string Path, string Target);
public sealed class UnregisterResult
{
    public List<string> PreservedFiles { get; } = [];
}
