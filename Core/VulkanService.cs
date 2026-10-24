using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;

namespace ReShadeCentralized;

public sealed record LayerRegistration(string Hive, string View, string Manifest, string Kind = "Implicit");
public sealed record RegistrySnapshot(LayerRegistration Registration, int? PreviousValue);
public sealed record VulkanInspection(
    bool IsApplied,
    string Message,
    IReadOnlyList<string> Consumers,
    string? Error = null);
public interface ILayerRegistry
{
    IEnumerable<LayerRegistration> Enumerate();

    int? Read(LayerRegistration key);

    void Write(LayerRegistration key, int? value);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsLayerRegistry(string registryRoot = @"SOFTWARE\Khronos\Vulkan") : ILayerRegistry
{
    private string KeyPath(LayerRegistration item) => registryRoot + "\\" + item.Kind + "Layers";

    private static RegistryKey OpenBase(LayerRegistration item) =>
        RegistryKey.OpenBaseKey(
        item.Hive == "HKLM" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
        item.View == "32" ? RegistryView.Registry32 : RegistryView.Registry64);

    public IEnumerable<LayerRegistration> Enumerate()
    {
        foreach (string hive in new[]
        {
            "HKLM",
            "HKCU"
        })
        {
            foreach (string view in Environment.Is64BitOperatingSystem ? new[]
            {
                "64",
                "32"
            }

                : new[]
            {
                "32"
            })
            {
                foreach (string kind in new[]
                {
                    "Implicit",
                    "Explicit"
                })
                {
                    using RegistryKey root = OpenBase(new(hive, view, ""));
                    using RegistryKey? key = root.OpenSubKey(registryRoot + "\\" + kind + "Layers");
                    foreach (string name in key?.GetValueNames() ?? [])
                    {
                        yield return new(hive, view, name, kind);
                    }
                }
            }
        }
    }

    public int? Read(LayerRegistration item)
    {
        using RegistryKey root = OpenBase(item);
        using RegistryKey? key = root.OpenSubKey(KeyPath(item));
        object? value = key?.GetValue(item.Manifest);
        if (value == null)
        {
            return null;
        }

        if (value is not int number)
        {
            throw new InvalidDataException("Vulkan registration is not a DWORD: " + item.Manifest);
        }

        return number;
    }

    public void Write(LayerRegistration item, int? value)
    {
        using RegistryKey root = OpenBase(item);
        if (value == null)
        {
            using RegistryKey? existing = root.OpenSubKey(KeyPath(item), true);
            existing?.DeleteValue(item.Manifest, false);
        }
        else
        {
            using RegistryKey key = root.CreateSubKey(KeyPath(item), true);
            key.SetValue(item.Manifest, value.Value, RegistryValueKind.DWord);
        }
    }
}

public sealed partial class VulkanService(ILayerRegistry registry)
{
    private sealed record RegisteredLayer(LayerRegistration Registration, string Library);

    private List<RegisteredLayer> RegisteredLayers()
    {
        var layers = new List<RegisteredLayer>();
        foreach (LayerRegistration registration in registry.Enumerate())
        {
            bool recognized = Path.GetFileName(registration.Manifest)
                .Contains("reshade", StringComparison.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(registration.Manifest))
                {
                    using var manifest = JsonDocument.Parse(File.ReadAllText(registration.Manifest));
                    recognized |= manifest.RootElement.TryGetProperty("layer", out JsonElement layer) &&
                        layer.TryGetProperty("name", out JsonElement name) && name.GetString() == "VK_LAYER_reshade";
                }

                string? library = RecognizedLibrary(registration.Manifest);
                if (library != null)
                {
                    layers.Add(new(registration, library));
                }
                else if (recognized)
                {
                    throw new InvalidDataException(
                        "Cannot verify the existing ReShade layer: " + registration.Manifest);
                }
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or
                InvalidOperationException or KeyNotFoundException or ArgumentException)
            {
                // An uncertain ReShade registration must not be replaced with a competing layer.
                if (recognized)
                {
                    throw new InvalidDataException("Check the existing Vulkan layer: " + registration.Manifest, error);
                }
            }
        }

        return layers;
    }

    private static string AppsPath(RegisteredLayer layer) =>
        Path.Combine(Path.GetDirectoryName(layer.Registration.Manifest)!, "ReShadeApps.ini");

    private static bool MatchesArchitecture(RegisteredLayer layer, string view) =>
        layer.Registration.View == view && PortableExecutable.Architecture(layer.Library) ==
            (view == "32" ? CpuArchitecture.X86 : CpuArchitecture.X64);

    public VulkanInspection Inspect(ManagerState? state)
    {
        const string missing = "No shared layer for one or both architectures. Choose Apply / repair.";
        if (state == null)
        {
            return new(false, missing, []);
        }

        var consumers = state.Games.Where(game => game.Api == RenderApi.Vulkan)
            .Select(game => game.Executable).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            List<RegisteredLayer> layers = RegisteredLayers();
            foreach (string path in layers.Select(AppsPath).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (string application in IniDocument.Split(IniDocument.Read(path).Get("", "Apps")))
                {
                    if (!state.Games.Any(game => game.Api != RenderApi.Vulkan &&
                        FileTransaction.Same(game.Executable, application)))
                    {
                        consumers.Add(application);
                    }
                }
            }

            string[] applications = consumers.Order(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (string view in new[] { "32", "64" })
            {
                RegisteredLayer[] existing = layers.Where(layer => MatchesArchitecture(layer, view)).ToArray();
                if (existing.Length == 0)
                {
                    return new(false, missing, applications);
                }

                if (!existing.Any(layer => layer.Registration.Kind == "Implicit" &&
                    registry.Read(layer.Registration) == 0))
                {
                    return new(false,
                        "An existing shared layer is disabled or explicit. Use its installer to enable it.", applications);
                }
            }

            bool external = layers.Any(layer => !OwnsRegistration(state, layer.Registration));
            string message = external ? "Using existing shared layers. Their runtimes and registrations are retained." :
                "Shared layer available (x86 and x64).";
            return new(true, message, applications);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
            JsonException or
            InvalidOperationException or KeyNotFoundException or ArgumentException or NotSupportedException or
            System.Security.SecurityException or System.ComponentModel.Win32Exception or BadImageFormatException)
        {
            return new(false, "Cannot verify the existing Vulkan layer; see Activity. Its registration was retained.",
                consumers.Order(StringComparer.OrdinalIgnoreCase).ToArray(), error.Message);
        }
    }

    public static string? RecognizedLibrary(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var json = JsonDocument.Parse(File.ReadAllText(path));
        if (!json.RootElement.TryGetProperty("layer", out JsonElement layer) ||
            !layer.TryGetProperty("name", out JsonElement name) ||
            name.GetString() != "VK_LAYER_reshade")
        {
            return null;
        }

        string library = layer.GetProperty("library_path").GetString() ??
            throw new InvalidDataException("Missing layer library path.");
        string resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, library));
        if (!PortableExecutable.IsReShade(resolved))
        {
            throw new InvalidDataException("Cannot verify the existing ReShade layer DLL: " + resolved);
        }

        return resolved;
    }

    public void Enable(ManagerState state, FileTransaction transaction)
    {
        List<RegisteredLayer> layers = RegisteredLayers();
        string binaries = state.Paths.Binaries(state.VulkanBuild);
        state.VulkanLayerDirectory ??= Path.Combine(state.Paths.Root, "Vulkan");
        foreach (string view in new[] { "32", "64" })
        {
            RegisteredLayer[] existing = layers.Where(layer => MatchesArchitecture(layer, view)).ToArray();
            if (existing.Length > 0)
            {
                foreach (RegisteredLayer owned in existing.Where(layer =>
                    OwnsRegistration(state, layer.Registration) && registry.Read(layer.Registration) == 0))
                {
                    UpdateOwnedLayer(state, owned.Registration.Manifest, binaries, view, transaction);
                }

                continue;
            }

            string manifest = Path.Combine(state.VulkanLayerDirectory, "ReShade" + view + ".json");
            UpdateOwnedLayer(state, manifest, binaries, view, transaction);
            var registration = new LayerRegistration("HKLM", view, manifest);
            // Never rewrite an existing registry value, including a disabled registration.
            if (registry.Read(registration) == null)
            {
                transaction.SetRegistry(registration, 0);
                if (!OwnsRegistration(state, registration))
                {
                    state.OwnedVulkanRegistrations.Add(registration);
                }
            }
        }

        state.VulkanManaged = true;
        WriteApps(state, transaction);
    }

    private static void UpdateOwnedLayer(
        ManagerState state,
        string manifest,
        string binaries,
        string view,
        FileTransaction transaction)
    {
        if (new[] { "ReShade32.dll", "ReShade64.dll", "ReShade32.json", "ReShade64.json" }
            .Any(name => !File.Exists(Path.Combine(binaries, name))))
        {
            throw new InvalidOperationException("The Vulkan runtime is not installed completely. " +
                "Open Runtime & storage > Update ReShade, then retry Apply / repair.");
        }

        RuntimeService.ValidatePayload(binaries, state.VulkanBuild);
        string directory = Path.GetDirectoryName(manifest)!;
        foreach (string extension in new[] { ".dll", ".json" })
        {
            string name = "ReShade" + view + extension;
            string target = Path.Combine(directory, name);
            FileTransaction.CheckParents(target);
            if (FileTransaction.Link(target) != null || (File.Exists(target) &&
                (!state.VulkanLayerFiles.TryGetValue(target, out string? hash) ||
                FileTransaction.Hash(target) != hash)))
            {
                throw new IOException("Shared layer file was changed outside this app; preserving it: " + target);
            }

            string source = Path.Combine(binaries, name);
            string sourceHash = FileTransaction.Hash(source);
            if (!File.Exists(target) || FileTransaction.Hash(target) != sourceHash)
            {
                transaction.Copy(source, target);
            }

            state.VulkanLayerFiles[target] = sourceHash;
        }
    }

    public void WriteApps(
        ManagerState state,
        FileTransaction transaction,
        bool enroll = true)
    {
        var desired = new List<VulkanAppEntry>();
        if (enroll)
        {
            List<RegisteredLayer> layers = RegisteredLayers();
            foreach (GameProfile game in state.Games.Where(game =>
                game.Api == RenderApi.Vulkan))
            {
                string view = game.Architecture == CpuArchitecture.X86 ? "32" : "64";
                RegisteredLayer? layer = layers.Where(layer => MatchesArchitecture(layer, view))
                    .OrderByDescending(layer => layer.Registration.Kind == "Implicit" &&
                        registry.Read(layer.Registration) == 0)
                    .FirstOrDefault();
                if (layer != null)
                {
                    desired.Add(new(AppsPath(layer), game.Executable));
                }
            }
        }
        else
        {
            desired.AddRange(state.OwnedVulkanApps.Where(entry => state.Games.Any(game =>
                game.Api == RenderApi.Vulkan &&
                FileTransaction.Same(game.Executable, entry.Executable))));
        }

        VulkanAppEntry[] owned = state.OwnedVulkanApps.Distinct().ToArray();
        var nextOwned = new List<VulkanAppEntry>();
        IEnumerable<string> paths = owned.Concat(desired).Select(entry => entry.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            var document = IniDocument.Read(path);
            string[] original = IniDocument.Split(document.Get("", "Apps"));
            var applications = original.ToList();
            foreach (VulkanAppEntry entry in owned.Where(entry => FileTransaction.Same(entry.Path, path)))
            {
                bool keep = desired.Any(item => FileTransaction.Same(item.Path, path) &&
                    FileTransaction.Same(item.Executable, entry.Executable));
                if (keep)
                {
                    nextOwned.Add(entry);
                }
                else
                {
                    applications.RemoveAll(application => FileTransaction.Same(application, entry.Executable));
                }
            }

            foreach (VulkanAppEntry entry in desired.Where(entry => FileTransaction.Same(entry.Path, path)))
            {
                if (!applications.Contains(entry.Executable, StringComparer.OrdinalIgnoreCase))
                {
                    applications.Add(entry.Executable);
                    nextOwned.Add(entry);
                }
            }

            if (!original.SequenceEqual(applications))
            {
                if (FileTransaction.Link(path) != null)
                {
                    throw new IOException("Application list is an unmanaged symbolic link: " + path);
                }

                document.Set("", "Apps", IniDocument.Join(applications));
                transaction.WriteText(path, document.ToString());
            }
        }

        state.OwnedVulkanApps = nextOwned.Distinct().ToList();
    }
}
