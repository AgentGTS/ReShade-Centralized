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

public sealed class VulkanService(ILayerRegistry registry)
{
    private static List<LayerRegistration> DesiredRegistrations(ManagerState state)
    {
        string binaries = state.Paths.Binaries(state.VulkanBuild);
        return
        [
            new("HKLM", "64", Path.Combine(binaries, "ReShade64.json")),
            new("HKLM", "32", Path.Combine(binaries, "ReShade32.json"))
        ];
    }

    private static bool SameRegistration(LayerRegistration first, LayerRegistration second) =>
        first.Hive.Equals(second.Hive, StringComparison.OrdinalIgnoreCase) && first.View == second.View &&
        first.Kind.Equals(second.Kind, StringComparison.OrdinalIgnoreCase) &&
        FileTransaction.Same(first.Manifest, second.Manifest);

    public VulkanInspection Inspect(ManagerState? state)
    {
        const string notApplied = "Not applied. Choose Apply / repair.";
        if (state == null)
        {
            return new(false, notApplied, []);
        }

        var consumers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (GameProfile game in state.Games.Where(game => game.Api == RenderApi.Vulkan &&
            game.Executable != null))
        {
            consumers.Add(game.Executable!);
        }

        void AddExternalConsumers(IEnumerable<string> applications)
        {
            foreach (string application in applications)
            {
                if (!state.Games.Any(game => game.Executable != null &&
                        FileTransaction.Same(game.Executable, application)))
                {
                    consumers.Add(application);
                }
            }
        }

        AddExternalConsumers(state.ExternalVulkanApps);
        List<LayerRegistration> desired = DesiredRegistrations(state);
        bool competing = false;
        string? inspectionError = null;
        try
        {
            foreach (LayerRegistration registration in registry.Enumerate())
            {
                bool isDesired = desired.Any(expected => SameRegistration(expected, registration));
                bool recognized = isDesired || Path.GetFileName(registration.Manifest)
                    .Contains("reshade", StringComparison.OrdinalIgnoreCase);
                try
                {
                    string? library = RecognizedLibrary(registration.Manifest);
                    if (library == null)
                    {
                        if (recognized)
                        {
                            inspectionError = "Cannot verify ReShade manifest: " + registration.Manifest;
                        }
                        continue;
                    }

                    recognized = true;
                    competing |= !isDesired;
                    string appsPath = Path.Combine(Path.GetDirectoryName(registration.Manifest)!, "ReShadeApps.ini");
                    if (File.Exists(appsPath))
                    {
                        AddExternalConsumers(IniDocument.Split(IniDocument.Read(appsPath).Get("", "Apps")));
                    }
                }
                catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException
                    or InvalidOperationException or KeyNotFoundException or ArgumentException)
                {
                    if (recognized)
                    {
                        inspectionError = registration.Manifest + ": " + error.Message;
                    }
                }
            }

            if (inspectionError != null)
            {
                return new(false, "Vulkan registration needs attention. Choose Apply / repair; see Activity.",
                    consumers.Order(StringComparer.OrdinalIgnoreCase).ToArray(), inspectionError);
            }

            string[] applications = consumers.Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (competing)
            {
                return new(false, "Another ReShade Vulkan layer is registered. Choose Apply / repair.", applications);
            }

            if (desired.Any(registration => registry.Read(registration) != 0))
            {
                return new(false, notApplied, applications);
            }

            RuntimeService.ValidatePayload(state.Paths.Binaries(state.VulkanBuild), state.VulkanBuild);
            return new(true, "Applied (x86 and x64).", applications);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or KeyNotFoundException or ArgumentException or NotSupportedException
            or System.Security.SecurityException or System.ComponentModel.Win32Exception or BadImageFormatException)
        {
            return new(false, "Cannot verify the Vulkan layer. Update ReShade, then Apply / repair; see Activity.",
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
        string binaries = state.Paths.Binaries(state.VulkanBuild);
        string[] requiredFiles = ["ReShade32.dll", "ReShade64.dll", "ReShade32.json", "ReShade64.json"];
        if (requiredFiles.Any(file => !File.Exists(Path.Combine(binaries, file))))
        {
            throw new InvalidOperationException("The Vulkan runtime is not installed completely. " +
                "Open Runtime & storage > Update ReShade, then retry Vulkan > Apply / repair.");
        }

        RuntimeService.ValidatePayload(binaries);
        List<LayerRegistration> desired = DesiredRegistrations(state);
        foreach (LayerRegistration? item in registry.Enumerate().ToArray())
        {
            string? library;
            try
            {
                library = RecognizedLibrary(item.Manifest);
            }
            catch (Exception error) when (error is JsonException or IOException
                or InvalidOperationException or KeyNotFoundException)
            {
                if (Path.GetFileName(item.Manifest).Contains("reshade", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "Existing ReShade registration needs attention: " + item.Manifest,
                        error);
                }

                continue;
            }

            if (library == null)
            {
                if (state.OwnedRegistrations.Contains(item) ||
                    Path.GetFileName(item.Manifest).StartsWith("ReShade", StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("Unverifiable ReShade Vulkan registration: " + item.Manifest);
                }

                continue;
            }

            // Capture the original installation without overwriting or deleting its files.
            transaction.Capture(item.Manifest);
            transaction.Capture(library);
            string appsPath = Path.Combine(Path.GetDirectoryName(item.Manifest)!, "ReShadeApps.ini");
            if (File.Exists(appsPath) && !state.OwnedRegistrations.Contains(item))
            {
                transaction.Capture(appsPath);
                foreach (string app in IniDocument.Split(IniDocument.Read(appsPath).Get("", "Apps")))
                {
                    if (!state.Games.Any(game => game.Executable != null &&
                        FileTransaction.Same(game.Executable, app)) &&
                        !state.ExternalVulkanApps.Contains(app, StringComparer.OrdinalIgnoreCase))
                    {
                        state.ExternalVulkanApps.Add(app);
                    }
                }
            }

            if (!desired.Any(expected => SameRegistration(expected, item)))
            {
                transaction.SetRegistry(item, null);
            }
        }

        foreach (LayerRegistration item in desired)
        {
            transaction.SetRegistry(item, 0);
        }

        state.OwnedRegistrations = desired;
        state.VulkanManaged = true;
        WriteApps(state, transaction);
    }

    public void WriteApps(ManagerState state, FileTransaction transaction)
    {
        string binaries = state.Paths.Binaries(state.VulkanBuild);
        var apps = new IniDocument();
        apps.Set(
            "",
            "Apps",
            IniDocument.Join(state.ExternalVulkanApps.Concat(state.Games.Where(game => game.Api == RenderApi.Vulkan &&
                        game.Executable != null)
                    .Select(game => game.Executable!))
                .Distinct(StringComparer.OrdinalIgnoreCase)));
        transaction.WriteText(Path.Combine(binaries, "ReShadeApps.ini"), apps.ToString());
    }

    public void RemoveIfUnused(ManagerState state, FileTransaction transaction)
    {
        if (!state.VulkanManaged || state.Games.Any(game => game.Api == RenderApi.Vulkan) ||
            state.ExternalVulkanApps.Count > 0)
        {
            return;
        }

        foreach (LayerRegistration item in state.OwnedRegistrations)
        {
            if (registry.Read(item) == null)
            {
                continue;
            }

            if (RecognizedLibrary(item.Manifest) == null)
            {
                throw new IOException("Owned Vulkan registration was replaced externally.");
            }

            transaction.SetRegistry(item, null);
        }

        state.OwnedRegistrations.Clear();
        state.VulkanManaged = false;
    }
}
