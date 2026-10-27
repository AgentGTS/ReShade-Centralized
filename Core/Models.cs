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
}

public sealed record OwnedLink(string Path, string Target);
public sealed record VulkanAppEntry(string Path, string Executable);
public sealed record ExistingInstallationNotice(string Message, bool BlocksInstallation);
public sealed record InstallationLink(string Path, string Target, bool ReplacesFile, string? PreviousTarget = null);

public sealed record InstallationPlan(
    string GameDirectory,
    string ProfileDirectory,
    IReadOnlyList<InstallationLink> Links,
    IReadOnlyList<string> RemovedLinks,
    IReadOnlyList<string> LocalSearchPaths,
    string? PresetSource,
    string Fingerprint,
    string? Conflict)
{
    public string? Executable { get; init; }
    public string? TargetReason { get; init; }
    public string? ConfigurationSource { get; init; }
    public CpuArchitecture? RuntimeArchitecture { get; init; }

    public bool RequiresConfirmation => Links.Any(link => link.ReplacesFile);

    public string Describe(bool includePaths = true)
    {
        string FileLabel(string path) => includePaths ? path :
            Executable != null && path.StartsWith(Path.GetDirectoryName(Executable)! + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(Path.GetDirectoryName(Executable)!, path) :
            Path.GetFileName(path);
        var lines = new List<string>();
        if (TargetReason != null)
        {
            lines.Add($"{TargetReason}: {Executable}");
        }
        if (Executable != null && !FileTransaction.Same(GameDirectory, Path.GetDirectoryName(Executable)!))
        {
            lines.Add("Install folder: " + GameDirectory);
        }
        if (RuntimeArchitecture != null)
        {
            lines.Add("Runtime architecture: " + (RuntimeArchitecture == CpuArchitecture.X64 ? "64-bit" : "32-bit"));
        }
        foreach (InstallationLink link in Links)
        {
            string operation = "Link";
            if (link.ReplacesFile)
            {
                operation = link.PreviousTarget == null ? "Replace file" : "Replace link";
            }
            if (includePaths)
            {
                string previous = link.PreviousTarget == null ? "" : $"\n  Current target: {link.PreviousTarget}";
                lines.Add($"{operation}: {link.Path}{previous}\n  -> {link.Target}");
            }
            else
            {
                string destination = FileTransaction.Same(link.Target, Path.Combine(ProfileDirectory, "ReShade.ini"))
                    ? "game profile" : "central runtime";
                lines.Add($"{operation}: {FileLabel(link.Path)} -> {destination} ({FileLabel(link.Target)}).");
            }
        }

        lines.AddRange(RemovedLinks.Select(path => "Remove managed link: " + FileLabel(path)));
        if (PresetSource != null)
        {
            lines.Add(includePaths
                ? $"Copy preset: {PresetSource}\n  -> {Path.Combine(ProfileDirectory, "ReShadePreset.ini")}"
                : $"Copy preset: {FileLabel(PresetSource)} -> game profile (ReShadePreset.ini).");
        }

        lines.AddRange(LocalSearchPaths.Select(path => "Keep local shaders/textures: " +
            (includePaths ? path : "reshade-shaders\\" + Path.GetFileName(path.TrimEnd('*', '\\', '/')))));
        if (RequiresConfirmation)
        {
            lines.Add("Replaced files will be deleted. Files that existing links point to are kept. " +
                "Unregister will not recover deleted files.");
        }

        if (Conflict != null)
        {
            string conflict = Conflict;
            if (!includePaths)
            {
                string[] paths = DeploymentService.ProxyNames.Append("ReShade.ini")
                    .Select(name => Path.Combine(GameDirectory, name))
                    .Concat(Links.Select(link => link.Path)).Concat(RemovedLinks)
                    .Distinct().OrderByDescending(path => path.Length).ToArray();
                foreach (string path in paths)
                {
                    conflict = conflict.Replace(path, FileLabel(path), StringComparison.OrdinalIgnoreCase);
                }
            }
            lines.Add(conflict);
        }

        return string.Join("\n\n", lines);
    }
}

public sealed class UnregisterResult
{
    public List<string> PreservedFiles { get; } = [];
}

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
}

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

    public ManagerState Clone() =>
        JsonSerializer.Deserialize<ManagerState>(
        JsonSerializer.Serialize(this, Json),
        Json)!;
}
