using System.Text.Json.Serialization;

namespace ReShadeCentralized;

public sealed class CentralPaths
{
    public string Root { get; set; } = "";

    [JsonIgnore]
    public string ShaderRoot => Path.Combine(Root, "reshade-shaders");

    [JsonIgnore]
    public string Shaders => Path.Combine(ShaderRoot, "shaders");

    [JsonIgnore]
    public string Textures => Path.Combine(ShaderRoot, "textures");

    [JsonIgnore]
    public string Profiles => Path.Combine(Root, "presets");

    [JsonIgnore]
    public string Screenshots => Path.Combine(Root, "screenshots");

    [JsonIgnore]
    public string StandardBinaries => Path.Combine(Root, "reshade-files");

    [JsonIgnore]
    public string AddonBinaries => Path.Combine(Root, "reshade-files-mod");

    public static CentralPaths At(string root)
    {
        root = Path.GetFullPath(root);
        return new()
        {
            Root = root
        };
    }

    public string Binaries(RuntimeBuild build) => build == RuntimeBuild.Standard ? StandardBinaries : AddonBinaries;

    public IEnumerable<string> Directories() =>
        [Shaders, Textures, Profiles, Screenshots, StandardBinaries, AddonBinaries];
}
