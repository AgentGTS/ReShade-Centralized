using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReShadeCentralized;

public enum RuntimeBuild {
    Standard,
    FullAddon
}

public sealed class RuntimeService(Downloads downloads) {
    public static readonly string[] PayloadFiles =
        ["ReShade32.dll", "ReShade64.dll", "ReShade32.json", "ReShade64.json"];

    public static IReadOnlyDictionary<RuntimeBuild, string> Discover(string html) {
        var result = new Dictionary<RuntimeBuild, string>();
        const string installerLinkPattern =
            """href\s*=\s*["'](?<url>(?:https://(?:www\.)?reshade\.me)?/downloads/ReShade_""" +
            """Setup_(?<version>[0-9]+\.[0-9]+\.[0-9]+)(?<addon>_Addon)?\.exe)["']""";

        foreach (Match match in Regex.Matches(html, installerLinkPattern, RegexOptions.IgnoreCase)) {
            RuntimeBuild build = match.Groups["addon"].Success ? RuntimeBuild.FullAddon : RuntimeBuild.Standard;
            string url = new Uri(new Uri("https://reshade.me"), match.Groups["url"].Value).AbsoluteUri;
            if (result.TryGetValue(build, out string? prior) && prior != url)
                throw new InvalidDataException("Ambiguous official ReShade download links.");

            result[build] = url;
        }

        if (result.Count != 2) {
            throw new InvalidDataException(
                "The official download page did not provide separate Standard and " +
                $"{DisplayNames.For(RuntimeBuild.FullAddon)} installers.");
        }

        return result;
    }

    public static string ValidatePayload(string directory, RuntimeBuild expectedBuild) {
        string? version = null;
        foreach (CpuArchitecture architecture in Enum.GetValues<CpuArchitecture>()) {
            string stem = architecture == CpuArchitecture.X86 ? "ReShade32" : "ReShade64";
            string dll = Path.Combine(directory, stem + ".dll");
            if (PortableExecutable.Architecture(dll) != architecture || !PortableExecutable.IsReShade(dll))
                throw new InvalidDataException("Unexpected ReShade DLL architecture or product identity.");

            using (FileStream image = File.OpenRead(dll)) {
                using var pe = new PEReader(image);
                bool signed = pe.PEHeaders.PEHeader!.CertificateTableDirectory.Size > 0;
                if (signed != (expectedBuild == RuntimeBuild.Standard)) {
                    throw new InvalidDataException(
                        "The installer does not match the selected Standard or " +
                        $"{DisplayNames.For(RuntimeBuild.FullAddon)} build.");
                }
            }

            string current = FileVersionInfo.GetVersionInfo(dll).ProductVersion?.Split('+')[0] ??
                throw new InvalidDataException("ReShade version is missing.");
            if (!Version.TryParse(current, out _))
                throw new InvalidDataException("Invalid ReShade version.");

            if (version != null && version != current)
                throw new InvalidDataException("32-bit and 64-bit ReShade versions do not match.");

            version = current;
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, stem + ".json")));
            JsonElement layer = manifest.RootElement.GetProperty("layer");
            string? library = layer.GetProperty("library_path").GetString()?.Replace('\\', '/');
            if (layer.GetProperty("name").GetString() != "VK_LAYER_reshade"
                || layer.GetProperty("type").GetString() != "GLOBAL"
                || (library != stem + ".dll" && library != "./" + stem + ".dll"))
                throw new InvalidDataException("Unexpected Vulkan manifest identity or library path.");
        }

        return version!;
    }

    public async Task StageAsync(
        string staging,
        IProgress<string>? progress,
        CancellationToken cancellationToken) {
        IReadOnlyDictionary<RuntimeBuild, string> links = Discover(
            await downloads.TextAsync(
                "https://reshade.me/",
                cancellationToken));
        string? releaseVersion = null;
        Directory.CreateDirectory(staging);
        foreach ((RuntimeBuild build, string url) in links) {
            string installer = Path.Combine(staging, build + ".exe");
            await downloads.FileAsync(url, installer, progress, cancellationToken, SafeArchive.MaximumInstallerBytes);
            cancellationToken.ThrowIfCancellationRequested();
            string output = Path.Combine(staging, build.ToString());
            SafeArchive.ExtractInstaller(installer, output, cancellationToken);
            string version = ValidatePayload(output, build);
            string expected = Regex.Match(new Uri(url).LocalPath, @"\d+\.\d+\.\d+").Value;
            if (version != expected)
                throw new InvalidDataException("Downloaded runtime version does not match its official link.");

            if (releaseVersion != null && version != releaseVersion)
                throw new InvalidDataException("Standard and add-on releases do not match.");

            releaseVersion = version;
        }
    }

    public static void Deploy(
        string staging,
        ManagerState state,
        FileTransaction transaction,
        CancellationToken cancellationToken) {
        var versions = Enum.GetValues<RuntimeBuild>().ToDictionary(
            build => build,
            build => ValidatePayload(Path.Combine(staging, build.ToString()), build));
        foreach ((RuntimeBuild build, string version) in versions) {
            string source = Path.Combine(staging, build.ToString());
            string destination = state.Paths.Binaries(build);
            state.RuntimeVersions[build] = version;
            foreach (string name in PayloadFiles) {
                cancellationToken.ThrowIfCancellationRequested();
                string incoming = Path.Combine(source, name);
                string target = Path.Combine(destination, name);
                LocalFiles.CheckParents(target);
                if (LocalFiles.Link(target) != null)
                    throw new IOException("Central runtime file is a symbolic link: " + target);

                bool exists = File.Exists(target);
                if (exists && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Central runtime file is a reparse point: " + target);

                string incomingHash = LocalFiles.Hash(incoming, cancellationToken);
                string? currentHash = exists ? LocalFiles.Hash(target, cancellationToken) : null;
                if (!string.Equals(currentHash, incomingHash, StringComparison.OrdinalIgnoreCase)) {
                    string? expected = exists ? state.RuntimeHashes.GetValueOrDefault(target) : null;
                    if ((exists && (expected == null
                            || !string.Equals(expected, currentHash, StringComparison.OrdinalIgnoreCase)))
                        || !transaction.CopyIfUnchanged(incoming, target, expected, cancellationToken)
                        || !string.Equals(LocalFiles.Hash(target, cancellationToken), incomingHash, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException("A modified or unmanaged file occupies the central runtime path: " +
                            target + ". Back it up or move it elsewhere before retrying.");
                    }
                }

                state.RuntimeHashes[target] = incomingHash;
            }
        }
    }
}

public sealed class RuntimeInstaller(CentralStorage storage, VulkanService vulkan, Downloads downloads) {
    public Task UpdateRuntimeAsync(IProgress<string>? progress, CancellationToken cancellationToken) =>
        DeployAsync("Update official ReShade runtimes", async staging => {
            await new RuntimeService(downloads).StageAsync(staging, progress, cancellationToken);
            progress?.Report("Validating and deploying both runtime builds\u2026");
        }, cancellationToken);

    public Task ImportRuntimeAsync(
        string standardInstaller,
        string addonInstaller,
        CancellationToken cancellationToken) =>
        DeployAsync("Import official ReShade installers", staging => {
            SafeArchive.ExtractInstaller(
                standardInstaller, Path.Combine(staging, RuntimeBuild.Standard.ToString()), cancellationToken);
            SafeArchive.ExtractInstaller(
                addonInstaller, Path.Combine(staging, RuntimeBuild.FullAddon.ToString()), cancellationToken);
            return Task.CompletedTask;
        }, cancellationToken);

    private Task DeployAsync(string description, Func<string, Task> stage, CancellationToken cancellationToken) =>
        storage.ChangeAsync(description, async (updatedState, transaction) => {
            using var staging = new StagingDirectory(storage.Store, storage.ReportWarning);
            await stage(staging.Path);

            RuntimeService.Deploy(staging.Path, updatedState, transaction, cancellationToken);
            if (updatedState.VulkanManaged)
                vulkan.RefreshLayers(updatedState, transaction);
        }, cancellationToken);
}
