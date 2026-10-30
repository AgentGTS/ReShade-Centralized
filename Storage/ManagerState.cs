using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReShadeCentralized;

public sealed class ManagerState
{
    public int SchemaVersion { get; set; } = StateStore.CurrentSchemaVersion;
    public CentralPaths Paths { get; set; } = new();

    public Dictionary<RuntimeBuild, string> RuntimeVersions { get; set; } = [];
    public Dictionary<string, string> RuntimeHashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<GameProfile> Games { get; set; } = [];
    public List<InstalledPackage> Packages { get; set; } = [];
    public List<string> SelectedPackages { get; set; } = [];
    public RuntimeBuild VulkanBuild { get; set; }

    public bool VulkanManaged { get; set; }

    public string? VulkanLayerDirectory { get; set; }
    public Dictionary<string, string> VulkanLayerFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<LayerRegistration> OwnedVulkanRegistrations { get; set; } = [];
    public List<VulkanAppEntry> OwnedVulkanApps { get; set; } = [];
    public static JsonSerializerOptions Json { get; } = new()
    {
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    public ManagerState Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        Paths = new() { Root = Paths.Root },
        RuntimeVersions = new(RuntimeVersions),
        RuntimeHashes = new(RuntimeHashes, StringComparer.OrdinalIgnoreCase),
        Games = Games.Select(game => game.Clone()).ToList(),
        Packages = Packages.Select(package => package.Clone()).ToList(),
        SelectedPackages = [.. SelectedPackages],
        VulkanBuild = VulkanBuild,
        VulkanManaged = VulkanManaged,
        VulkanLayerDirectory = VulkanLayerDirectory,
        VulkanLayerFiles = new(VulkanLayerFiles, StringComparer.OrdinalIgnoreCase),
        OwnedVulkanRegistrations = [.. OwnedVulkanRegistrations],
        OwnedVulkanApps = [.. OwnedVulkanApps]
    };
}
