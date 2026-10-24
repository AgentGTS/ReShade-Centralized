namespace ReShadeCentralized;

public sealed class VulkanRemovalResult
{
    public List<LayerRegistration> Removed { get; } = [];
    public List<string> Preserved { get; } = [];
}

public sealed partial class VulkanService
{
    private static bool SameRegistration(LayerRegistration first, LayerRegistration second) =>
        first.Hive.Equals(second.Hive, StringComparison.OrdinalIgnoreCase) &&
        first.View == second.View && first.Kind.Equals(second.Kind, StringComparison.OrdinalIgnoreCase) &&
        FileTransaction.Same(first.Manifest, second.Manifest);

    private static bool OwnsRegistration(ManagerState state, LayerRegistration registration) =>
        state.OwnedVulkanRegistrations.Any(owned => SameRegistration(owned, registration));

    public VulkanRemovalResult RemoveOwnedRegistrations(ManagerState state, FileTransaction transaction)
    {
        if (state.Games.Any(game => game.Api == RenderApi.Vulkan))
        {
            throw new InvalidOperationException("Unregister the app's Vulkan games before removing its shared layer.");
        }

        var result = new VulkanRemovalResult();
        string directory = Path.Combine(state.Paths.Root, "Vulkan");
        string appsPath = Path.Combine(directory, "ReShadeApps.ini");
        if (state.OwnedVulkanRegistrations.Count > 0)
        {
            FileTransaction.CheckParents(appsPath);
            if (FileTransaction.Link(appsPath) != null)
            {
                throw new IOException("The shared layer's application list was replaced by a link; keeping the layer: " +
                    appsPath);
            }
            string[] consumers = IniDocument.Split(IniDocument.Read(appsPath).Get("", "Apps"));
            if (consumers.Length > 0)
            {
                throw new InvalidOperationException("Other games are still recorded as using this layer. " +
                    "Keep it installed. Application list: " + appsPath + "\n" + string.Join("\n", consumers));
            }
        }

        foreach (LayerRegistration owned in state.OwnedVulkanRegistrations.ToArray())
        {
            string? reason = null;
            try
            {
                string manifest = Path.Combine(directory, "ReShade" + owned.View + ".json");
                string library = Path.Combine(directory, "ReShade" + owned.View + ".dll");
                if (owned.Hive != "HKLM" || owned.Kind != "Implicit" || owned.View is not ("32" or "64") ||
                    !FileTransaction.Same(owned.Manifest, manifest))
                {
                    reason = "Ownership does not match an app-created registration";
                }
                else
                {
                    int? value = registry.Read(owned);
                    if (value == null)
                    {
                        state.OwnedVulkanRegistrations.RemoveAll(item => SameRegistration(item, owned));
                        continue;
                    }
                    if (value != 0)
                    {
                        reason = "Registry value was changed outside the app";
                    }
                    foreach (string path in new[] { manifest, library })
                    {
                        FileTransaction.CheckParents(path);
                        if (!state.VulkanLayerFiles.TryGetValue(path, out string? hash) ||
                            FileTransaction.Link(path) != null || Directory.Exists(path) ||
                            (File.Exists(path) && FileTransaction.Hash(path) != hash))
                        {
                            reason = "Layer files were changed or their ownership cannot be verified";
                        }
                    }
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
                ArgumentException or System.Security.SecurityException or System.ComponentModel.Win32Exception)
            {
                reason = "Cannot verify ownership: " + error.Message;
            }

            if (reason != null)
            {
                result.Preserved.Add(reason + ": " + owned.Manifest);
                continue;
            }

            transaction.SetRegistry(owned, null);
            state.OwnedVulkanRegistrations.RemoveAll(item => SameRegistration(item, owned));
            result.Removed.Add(owned);
        }

        state.VulkanManaged = state.OwnedVulkanRegistrations.Count > 0;
        return result;
    }
}
