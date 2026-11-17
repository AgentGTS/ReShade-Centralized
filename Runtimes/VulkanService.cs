using System.Text.Json;

namespace ReShadeCentralized;

public sealed record VulkanInspection(
    bool IsApplied,
    string Message,
    IReadOnlyList<string> Consumers,
    string? Error = null);

public sealed class VulkanRemovalResult {
    public int RemovedCount { get; internal set; }
    public List<string> Preserved { get; } = [];
}

public sealed partial class VulkanService(ILayerRegistry registry) {
    private const string DisabledLayer = "An existing shared layer is disabled or explicit. Use its installer to enable it.";

    private sealed record RegisteredLayer(LayerRegistration Registration, string Library);

    private List<RegisteredLayer> RegisteredLayers() {
        var layers = new List<RegisteredLayer>();
        foreach (LayerRegistration registration in registry.Enumerate()) {
            bool recognized = Path.GetFileName(registration.Manifest)
                .Contains("reshade", StringComparison.OrdinalIgnoreCase);
            try {
                if (File.Exists(registration.Manifest)) {
                    using var manifest = JsonDocument.Parse(ConfigurationService.ReadConfigurationText(
                        ConfigurationService.ReadConfigurationBytes(registration.Manifest)));
                    if (manifest.RootElement.TryGetProperty("layer", out JsonElement layer)
                        && layer.TryGetProperty("name", out JsonElement name)
                        && name.GetString() == "VK_LAYER_reshade") {
                        recognized = true;
                        string library = layer.GetProperty("library_path").GetString() ??
                            throw new InvalidDataException("Missing layer library path.");
                        string resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(registration.Manifest)!, library));
                        if (!PortableExecutable.IsReShade(resolved))
                            throw new InvalidDataException("Cannot verify the existing ReShade layer DLL: " + resolved);

                        layers.Add(new(registration, resolved));
                        continue;
                    }
                }

                if (recognized) {
                    throw new InvalidDataException(
                        "Cannot verify the existing ReShade layer: " + registration.Manifest);
                }
            } catch (Exception error) when (IsLayerInspectionError(error)) {
                // An uncertain ReShade registration must not be replaced with a competing layer.
                if (recognized)
                    throw new InvalidDataException("Check the existing Vulkan layer: " + registration.Manifest, error);
            }
        }

        return layers;
    }

    private static string AppsPath(RegisteredLayer layer) =>
        Path.Combine(Path.GetDirectoryName(layer.Registration.Manifest)!, "ReShadeApps.ini");

    private static bool MatchesArchitecture(RegisteredLayer layer, string view) =>
        layer.Registration.View == view && PortableExecutable.Architecture(layer.Library) ==
            (view == "32" ? CpuArchitecture.X86 : CpuArchitecture.X64);

    private bool IsEnabled(RegisteredLayer layer) =>
        layer.Registration.Kind == "Implicit" && registry.Read(layer.Registration) == 0;

    public VulkanInspection Inspect(ManagerState? state) {
        const string missing = "No shared layer for one or both architectures. Choose Apply / repair.";
        if (state == null)
            return new(false, missing, []);

        var consumers = state.Games.Where(game => game.Api == RenderApi.Vulkan)
            .Select(game => game.Executable)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        try {
            List<RegisteredLayer> layers = RegisteredLayers();
            foreach (string path in layers.Select(AppsPath).Distinct(StringComparer.OrdinalIgnoreCase)) {
                foreach (string application in ReadAppEntries(path)) {
                    if (!state.Games.Any(game => game.Api != RenderApi.Vulkan
                        && LocalFiles.Same(game.Executable, application)))
                        consumers.Add(application);
                }
            }

            string[] applications = consumers.Order(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (string view in new[] { "32", "64" }) {
                RegisteredLayer[] existing = layers.Where(layer => MatchesArchitecture(layer, view)).ToArray();
                if (existing.Length == 0)
                    return new(false, missing, applications);

                if (!existing.Any(IsEnabled))
                    return new(false, DisabledLayer, applications);
            }

            bool external = layers.Any(layer => !OwnsRegistration(state, layer.Registration));
            string message = external
                ? "Using existing shared layers. Their runtimes and registrations are retained."
                : "Shared layer available (x86 and x64).";
            return new(true, message, applications);
        } catch (Exception error) when (IsLayerInspectionError(error)) {
            return new(
                false,
                "Cannot verify the existing Vulkan layer; see Activity. Its registration was retained.",
                consumers.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                error.Message);
        }
    }

    private static bool IsLayerInspectionError(Exception error) => error is IOException
        or InvalidDataException
        or UnauthorizedAccessException
        or JsonException
        or InvalidOperationException
        or KeyNotFoundException
        or ArgumentException
        or NotSupportedException
        or System.Security.SecurityException
        or System.ComponentModel.Win32Exception
        or BadImageFormatException;

    public Task SetVulkanBuildAsync(CentralStorage storage, RuntimeBuild build, CancellationToken cancellationToken) {
        return storage.ChangeAsync(
            "Switch global Vulkan build to " + DisplayNames.For(build),
            (updatedState, transaction) => {
                updatedState.VulkanBuild = build;
                foreach (GameProfile game in updatedState.Games.Where(game => game.Api == RenderApi.Vulkan)) {
                    game.Build = build;
                }

                Enable(updatedState, transaction);
                return Task.CompletedTask;
            },
            cancellationToken);
    }

    public void Enable(
        ManagerState state,
        FileTransaction transaction,
        GameProfile? game = null,
        string? previousExecutable = null) =>
        EnsureLayers(state, transaction, requireBothViews: game == null, game, previousExecutable);

    public void RefreshLayers(ManagerState state, FileTransaction transaction) =>
        EnsureLayers(state, transaction, requireBothViews: false, null, null);

    private void EnsureLayers(
        ManagerState state,
        FileTransaction transaction,
        bool requireBothViews,
        GameProfile? game,
        string? previousExecutable) {
        string binaries = state.Paths.Binaries(state.VulkanBuild);
        state.VulkanLayerDirectory ??= Path.Combine(state.Paths.Root, "Vulkan");
        RestoreMissingOwnedLayers(state, binaries, transaction);
        List<RegisteredLayer> layers = RegisteredLayers();

        foreach (string view in new[] { "32", "64" }) {
            RegisteredLayer[] existing = layers.Where(layer => MatchesArchitecture(layer, view)).ToArray();
            if (existing.Length > 0) {
                RegisteredLayer[] enabled = existing.Where(IsEnabled).ToArray();
                if (enabled.Length == 0
                    && (requireBothViews || game != null && view == ViewFor(game.Architecture)))
                    throw new InvalidOperationException(DisabledLayer);

                foreach (RegisteredLayer owned in enabled.Where(layer => OwnsRegistration(state, layer.Registration))) {
                    UpdateOwnedLayer(state, owned.Registration.Manifest, binaries, view, transaction);
                }

                continue;
            }

            string manifest = Path.Combine(state.VulkanLayerDirectory, "ReShade" + view + ".json");
            var registration = new LayerRegistration("HKLM", view, manifest);

            UpdateOwnedLayer(state, manifest, binaries, view, transaction);

            // Never rewrite an existing registry value, including a disabled registration.
            if (registry.Read(registration) != null)
                throw new IOException("The Vulkan registration changed during this operation: " + manifest);

            transaction.SetRegistry(registration, 0);
            if (!OwnsRegistration(state, registration))
                state.OwnedVulkanRegistrations.Add(registration);
        }

        state.VulkanManaged = true;
        WriteApps(state, transaction, enroll: true, game, previousExecutable);
    }

    private void RestoreMissingOwnedLayers(ManagerState state, string binaries, FileTransaction transaction) {
        foreach (LayerRegistration owned in state.OwnedVulkanRegistrations) {
            if (!IsAppRegistration(state, owned) || registry.Read(owned) != 0)
                continue;

            string manifest = owned.Manifest;
            string library = Path.ChangeExtension(manifest, ".dll");
            if (File.Exists(manifest) && File.Exists(library))
                continue;

            // Both contents must have been tracked before a missing file can be safely recreated.
            if (!state.VulkanLayerFiles.ContainsKey(manifest) || !state.VulkanLayerFiles.ContainsKey(library))
                throw new IOException("Cannot verify ownership of the incomplete Vulkan layer: " + manifest);

            UpdateOwnedLayer(state, manifest, binaries, owned.View, transaction);
        }
    }

    private static void UpdateOwnedLayer(
        ManagerState state,
        string manifest,
        string binaries,
        string view,
        FileTransaction transaction) {
        if (RuntimeService.PayloadFiles
            .Any(name => !File.Exists(Path.Combine(binaries, name)))) {
            throw new InvalidOperationException("The Vulkan runtime is not installed completely. " +
                "Open Runtime & storage > Update ReShade, then retry Apply / repair.");
        }

        RuntimeService.ValidatePayload(binaries, state.VulkanBuild);
        string directory = Path.GetDirectoryName(manifest)!;
        foreach (string extension in new[] { ".dll", ".json" }) {
            string name = "ReShade" + view + extension;
            string target = Path.Combine(directory, name);
            LocalFiles.CheckParents(target);
            bool exists = File.Exists(target);
            state.VulkanLayerFiles.TryGetValue(target, out string? hash);
            if (LocalFiles.Link(target) != null
                || Directory.Exists(target)
                || (exists && (hash == null || LocalFiles.Hash(target) != hash)))
                throw new IOException("Shared layer file was changed outside this app; preserving it: " + target);

            string source = Path.Combine(binaries, name);
            string sourceHash = LocalFiles.Hash(source);
            if ((!exists || hash != sourceHash) && !transaction.CopyIfUnchanged(source, target, exists ? hash : null))
                throw new IOException("Shared layer file changed during the operation; preserving it: " + target);

            state.VulkanLayerFiles[target] = sourceHash;
        }
    }

    private static bool SameRegistration(LayerRegistration first, LayerRegistration second) =>
        string.Equals(first.Hive, second.Hive, StringComparison.OrdinalIgnoreCase)
        && first.View == second.View
        && string.Equals(first.Kind, second.Kind, StringComparison.OrdinalIgnoreCase)
        && SameAbsolutePath(first.Manifest, second.Manifest);

    private static bool OwnsRegistration(ManagerState state, LayerRegistration registration) =>
        IsAppRegistration(state, registration)
        && state.OwnedVulkanRegistrations.Any(owned => SameRegistration(owned, registration));

    private static bool IsAppRegistration(ManagerState state, LayerRegistration registration) =>
        string.Equals(registration.Hive, "HKLM", StringComparison.OrdinalIgnoreCase)
        && string.Equals(registration.Kind, "Implicit", StringComparison.OrdinalIgnoreCase)
        && registration.View is "32" or "64"
        && SameAbsolutePath(registration.Manifest,
            Path.Combine(state.Paths.Root, "Vulkan", "ReShade" + registration.View + ".json"));

    public async Task<VulkanRemovalResult> RemoveVulkanSupportAsync(
        CentralStorage storage,
        CancellationToken cancellationToken) {
        VulkanRemovalResult? result = null;
        await storage.ChangeAsync("Remove app-owned Vulkan layer registrations", (updatedState, transaction) => {
            result = RemoveOwnedRegistrations(updatedState, transaction);
            return Task.CompletedTask;
        }, cancellationToken);
        return result!;
    }

    public VulkanRemovalResult RemoveOwnedRegistrations(ManagerState state, FileTransaction transaction) {
        if (state.Games.Any(game => game.Api == RenderApi.Vulkan))
            throw new InvalidOperationException("Unregister the app's Vulkan games before removing its shared layer.");

        var result = new VulkanRemovalResult();
        string directory = Path.Combine(state.Paths.Root, "Vulkan");
        string appsPath = Path.Combine(directory, "ReShadeApps.ini");
        if (state.OwnedVulkanRegistrations.Count > 0) {
            LocalFiles.CheckParents(appsPath);
            if (LocalFiles.Link(appsPath) != null) {
                throw new IOException("The shared layer's application list was replaced by a link; keeping the layer: " +
                    appsPath);
            }

            string[] consumers = ReadAppEntries(appsPath);
            if (consumers.Length > 0) {
                throw new InvalidOperationException("Other games are still recorded as using this layer. " +
                    "Keep it installed. Application list: " + appsPath + "\n" + string.Join("\n", consumers));
            }
        }

        foreach (LayerRegistration owned in state.OwnedVulkanRegistrations.ToArray()) {
            string? reason = null;
            try {
                string manifest = Path.Combine(directory, "ReShade" + owned.View + ".json");
                string library = Path.Combine(directory, "ReShade" + owned.View + ".dll");
                if (!IsAppRegistration(state, owned))
                    reason = "Ownership does not match an app-created registration";
                else {
                    int? value = registry.Read(owned);
                    if (value == null) {
                        state.OwnedVulkanRegistrations.RemoveAll(item => SameRegistration(item, owned));
                        continue;
                    }

                    if (value != 0)
                        reason = "Registry value was changed outside the app";

                    foreach (string path in new[] { manifest, library }) {
                        LocalFiles.CheckParents(path);
                        if (!state.VulkanLayerFiles.TryGetValue(path, out string? hash)
                            || LocalFiles.Link(path) != null
                            || Directory.Exists(path)
                            || (File.Exists(path) && LocalFiles.Hash(path) != hash))
                            reason = "Layer files were changed or their ownership cannot be verified";
                    }
                }
            } catch (Exception error) when (error is IOException
                or InvalidDataException
                or UnauthorizedAccessException
                or ArgumentException
                or System.Security.SecurityException
                or System.ComponentModel.Win32Exception) {
                reason = "Cannot verify ownership: " + error.Message;
            }

            if (reason != null) {
                result.Preserved.Add(reason + ": " + owned.Manifest);
                continue;
            }

            transaction.SetRegistry(owned, null);
            state.OwnedVulkanRegistrations.RemoveAll(item => SameRegistration(item, owned));
            result.RemovedCount++;
        }

        state.VulkanManaged = state.OwnedVulkanRegistrations.Count > 0;
        return result;
    }
}
