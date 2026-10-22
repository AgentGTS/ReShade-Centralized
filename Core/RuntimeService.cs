using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReShadeCentralized;

public sealed class RuntimeService(Downloads downloads)
{
    public static IReadOnlyDictionary<RuntimeBuild, string> Discover(string html)
    {
        var result = new Dictionary<RuntimeBuild, string>();
        const string installerLinkPattern =
            """href\s*=\s*["'](?<url>(?:https://(?:www\.)?reshade\.me)?/downloads/ReShade_""" +
            """Setup_(?<version>[0-9]+\.[0-9]+\.[0-9]+)(?<addon>_Addon)?\.exe)["']""";
        foreach (Match match in Regex.Matches(html, installerLinkPattern, RegexOptions.IgnoreCase))
        {
            RuntimeBuild build = match.Groups["addon"].Success ? RuntimeBuild.FullAddon : RuntimeBuild.Standard;
            string url = new Uri(new Uri("https://reshade.me"), match.Groups["url"].Value).AbsoluteUri;
            if (result.TryGetValue(build, out string? prior) && prior != url)
            {
                throw new InvalidDataException("Ambiguous official ReShade download links.");
            }

            result[build] = url;
        }

        if (result.Count != 2)
        {
            throw new InvalidDataException(
                "The official download page did not provide distinct standard and full-add-on installers.");
        }

        return result;
    }

    public static string ValidatePayload(string directory, RuntimeBuild? expectedBuild = null)
    {
        string? version = null;
        foreach (CpuArchitecture architecture in Enum.GetValues<CpuArchitecture>())
        {
            string stem = architecture == CpuArchitecture.X86 ? "ReShade32" : "ReShade64";
            string dll = Path.Combine(directory, stem + ".dll");
            if (PortableExecutable.Architecture(dll) != architecture || !PortableExecutable.IsReShade(dll))
            {
                throw new InvalidDataException("Unexpected ReShade DLL architecture or product identity.");
            }

            if (expectedBuild != null)
            {
                using FileStream image = File.OpenRead(dll);
                using var pe = new PEReader(image);
                bool signed = pe.PEHeaders.PEHeader!.CertificateTableDirectory.Size > 0;
                if (signed != (expectedBuild == RuntimeBuild.Standard))
                {
                    throw new InvalidDataException(
                        "The installer does not match the selected standard/full-add-on build.");
                }
            }

            string current = FileVersionInfo.GetVersionInfo(dll).ProductVersion?.Split('+')[0] ??
                throw new InvalidDataException("ReShade version is missing.");
            if (!Version.TryParse(current, out _))
            {
                throw new InvalidDataException("Invalid ReShade version.");
            }

            if (version != null && version != current)
            {
                throw new InvalidDataException("32-bit and 64-bit ReShade versions do not match.");
            }

            version = current;
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, stem + ".json")));
            JsonElement layer = manifest.RootElement.GetProperty("layer");
            string library = layer.GetProperty("library_path").GetString() ?? "";
            if (layer.GetProperty("name").GetString() != "VK_LAYER_reshade" ||
                layer.GetProperty("type").GetString() != "GLOBAL" ||
                (library.Replace('\\', '/') != stem + ".dll" &&
                library.Replace('\\', '/') != "./" + stem + ".dll"))
            {
                throw new InvalidDataException("Unexpected Vulkan manifest identity or library path.");
            }
        }

        return version!;
    }

    public async Task<Dictionary<RuntimeBuild, string>> StageAsync(
        string staging,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<RuntimeBuild, string> links = Discover(await downloads.TextAsync(
                "https://reshade.me/",
                cancellationToken));
        var versions = new Dictionary<RuntimeBuild, string>();
        Directory.CreateDirectory(staging);
        foreach ((RuntimeBuild build, string? url) in links)
        {
            string installer = Path.Combine(staging, build + ".exe");
            await downloads.FileAsync(url, installer, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            string output = Path.Combine(staging, build.ToString());
            SafeArchive.ExtractInstaller(installer, output);
            versions[build] = ValidatePayload(output, build);
            string expected = Regex.Match(new Uri(url).LocalPath, @"\d+\.\d+\.\d+").Value;
            if (versions[build] != expected)
            {
                throw new InvalidDataException("Downloaded runtime version does not match its official link.");
            }
        }

        if (versions.Values.Distinct().Count() != 1)
        {
            throw new InvalidDataException("Standard and add-on releases do not match.");
        }

        return versions;
    }

    public static void Deploy(
        string staging,
        ManagerState state,
        FileTransaction transaction,
        CancellationToken cancellationToken)
    {
        foreach (RuntimeBuild build in Enum.GetValues<RuntimeBuild>())
        {
            string source = Path.Combine(staging, build.ToString());
            state.RuntimeVersions[build] = ValidatePayload(source, build);
            foreach (string name in new[]
            {
                "ReShade32.dll",
                "ReShade64.dll",
                "ReShade32.json",
                "ReShade64.json"
            })
            {
                cancellationToken.ThrowIfCancellationRequested();
                string target = Path.Combine(state.Paths.Binaries(build), name);
                if (FileTransaction.Link(target) != null)
                {
                    throw new IOException("Central runtime file is a symbolic link: " + target);
                }

                if (name.EndsWith(".dll") && File.Exists(target) && !PortableExecutable.IsReShade(target))
                {
                    throw new IOException("An unrelated DLL occupies the central runtime path: " + target);
                }

                transaction.Copy(Path.Combine(source, name), target);
                state.RuntimeHashes[target] = FileTransaction.Hash(target);
            }
        }
    }
}
