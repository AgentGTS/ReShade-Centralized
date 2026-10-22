using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReShadeCentralized;

public enum RenderApi
{
    Direct3D9,
    DXGI,
    OpenGL,
    Vulkan
}

public enum RuntimeBuild
{
    Standard,
    FullAddon
}

public enum CpuArchitecture
{
    X86,
    X64
}

public sealed class CentralPaths
{
    public string Root { get; set; } = "";
    public string Shaders { get; set; } = "";
    public string Textures { get; set; } = "";
    public string Profiles { get; set; } = "";
    public string Screenshots { get; set; } = "";
    public string StandardBinaries { get; set; } = "";
    public string AddonBinaries { get; set; } = "";

    public static CentralPaths At(string root)
    {
        root = Path.GetFullPath(root);
        return new()
        {
            Root = root,
            Shaders = Path.Combine(root, "reshade-shaders", "shaders"),
            Textures = Path.Combine(root, "reshade-shaders", "textures"),
            Profiles = Path.Combine(root, "presets"),
            Screenshots = Path.Combine(root, "screenshots"),
            StandardBinaries = Path.Combine(root, "reshade-files"),
            AddonBinaries = Path.Combine(root, "reshade-files-mod")
        };
    }

    public string Binaries(RuntimeBuild build) => build == RuntimeBuild.Standard ? StandardBinaries : AddonBinaries;

    public IEnumerable<string> Directories() =>
        [Shaders, Textures, Profiles, Screenshots, StandardBinaries, AddonBinaries];
}

public sealed class GameProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string? Executable { get; set; }

    public RenderApi Api { get; set; }

    public RuntimeBuild Build { get; set; }

    public CpuArchitecture Architecture { get; set; }

    public string Directory { get; set; } = "";
    public List<OwnedLink> Links { get; set; } = [];
}

public sealed record OwnedLink(string Path, string Target);
public sealed record ExistingInstallationNotice(string Message, bool BlocksInstallation);

public sealed class PackageUpdateReport
{
    public int InstalledCount { get; internal set; }
    public int FailedCount { get; internal set; }
    public List<string> Messages { get; } = [];
    public string Summary => $"Shader packages: {InstalledCount} installed, {FailedCount} failed.";
}

public sealed class InstalledPackage
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Repository { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public DateTimeOffset Updated { get; set; }

    public Dictionary<string, string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ManagerState
{
    public int SchemaVersion { get; set; } = 1;
    public CentralPaths Paths { get; set; } = new();
    public string? ConfigurationTemplate { get; set; }

    public Dictionary<RuntimeBuild, string> RuntimeVersions { get; set; } = [];
    public Dictionary<string, string> RuntimeHashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<GameProfile> Games { get; set; } = [];
    public List<InstalledPackage> Packages { get; set; } = [];
    public List<string> SelectedPackages { get; set; } = [];
    public RuntimeBuild VulkanBuild { get; set; }

    public bool VulkanManaged { get; set; }

    public List<string> ExternalVulkanApps { get; set; } = [];
    public List<LayerRegistration> OwnedRegistrations { get; set; } = [];
    public static JsonSerializerOptions Json { get; } = new()
    {
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    public ManagerState Clone() =>
        JsonSerializer.Deserialize<ManagerState>(
        JsonSerializer.Serialize(this, Json),
        Json)!;
}
