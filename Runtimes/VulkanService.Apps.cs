using System.Security.Cryptography;

namespace ReShadeCentralized;

public sealed record VulkanAppEntry(string Path, string Executable);

public sealed partial class VulkanService {
    public void WriteApps(
        ManagerState state,
        FileTransaction transaction,
        bool enroll = true) => WriteApps(state, transaction, enroll, null, null);

    private static string ViewFor(CpuArchitecture architecture) => architecture == CpuArchitecture.X86 ? "32" : "64";

    private void WriteApps(
        ManagerState state,
        FileTransaction transaction,
        bool enroll,
        GameProfile? selectedGame,
        string? previousExecutable) {
        if (!enroll && state.OwnedVulkanApps.Count == 0)
            return;

        var desired = new List<VulkanAppEntry>();
        var unavailableExecutables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<RegisteredLayer> layers = RegisteredLayers();
        if (enroll) {
            foreach (GameProfile game in state.Games.Where(game => game.Api == RenderApi.Vulkan
                && (selectedGame == null || game.Id == selectedGame.Id))) {
                string view = ViewFor(game.Architecture);
                RegisteredLayer? layer = layers.FirstOrDefault(layer => MatchesArchitecture(layer, view)
                    && IsEnabled(layer));
                if (layer != null)
                    desired.Add(new(AppsPath(layer), game.Executable));
                else if (selectedGame != null)
                    throw new InvalidOperationException("No enabled shared Vulkan layer is available for " + game.Name + ".");
                else
                    unavailableExecutables.Add(game.Executable);
            }
        } else {
            desired.AddRange(state.OwnedVulkanApps.Where(entry => state.Games.Any(game =>
                game.Api == RenderApi.Vulkan
                && LocalFiles.Same(game.Executable, entry.Executable))));
        }

        VulkanAppEntry[] owned = state.OwnedVulkanApps.Where(entry =>
            !unavailableExecutables.Contains(entry.Executable) && (selectedGame == null
                || SameAbsolutePath(entry.Executable, selectedGame.Executable)
                || SameAbsolutePath(entry.Executable, previousExecutable))).Distinct().ToArray();
        var nextOwned = state.OwnedVulkanApps.Except(owned).ToList();
        IEnumerable<string> paths = owned.Concat(desired)
            .Select(entry => entry.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (string path in paths) {
            if (!layers.Any(layer => SameAbsolutePath(path, AppsPath(layer))))
                throw new IOException("Cannot verify the registered Vulkan application list; preserving it: " + path);

            var (document, original, hash) = ReadEditableAppList(path);
            var applications = original.ToList();

            foreach (VulkanAppEntry entry in owned.Where(entry => LocalFiles.Same(entry.Path, path))) {
                bool keep = desired.Any(item => LocalFiles.Same(item.Path, path)
                    && LocalFiles.Same(item.Executable, entry.Executable));
                if (keep)
                    nextOwned.Add(entry);
                else
                    applications.RemoveAll(application => LocalFiles.Same(application, entry.Executable));
            }

            foreach (VulkanAppEntry entry in desired.Where(entry => LocalFiles.Same(entry.Path, path))) {
                if (!applications.Contains(entry.Executable, StringComparer.OrdinalIgnoreCase)) {
                    applications.Add(entry.Executable);
                    nextOwned.Add(entry);
                }
            }

            if (!original.SequenceEqual(applications)) {
                document.Set("", "Apps", IniDocument.Join(applications));
                bool written = hash != null
                    ? transaction.RewriteIfUnchanged(path, hash, document.ToString())
                    : transaction.WriteTextIfAbsent(path, document.ToString());
                if (!written)
                    throw new IOException("Vulkan application list changed during the operation; preserving it: " + path);
            }
        }

        state.OwnedVulkanApps = nextOwned.Distinct().ToList();
    }

    public void UnregisterGame(
        ManagerState state,
        GameProfile game,
        FileTransaction transaction,
        UnregisterResult result) {
        if (game.Api != RenderApi.Vulkan)
            return;

        VulkanAppEntry[] owned = state.OwnedVulkanApps
            .Where(entry => SameAbsolutePath(entry.Executable, game.Executable))
            .ToArray();
        if (owned.Length == 0)
            return;

        string[] registeredLists;
        try {
            registeredLists = RegisteredLayers()
                .Select(AppsPath)
                .ToArray();
        } catch (Exception error) when (IsLayerInspectionError(error)) {
            foreach (VulkanAppEntry entry in owned) {
                result.PreservedFiles.Add(entry.Path);
            }

            return;
        }

        var retryable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void PreserveForRetry(string path) {
            result.PreservedFiles.Add(path);
            retryable.Add(path);
        }

        foreach (string path in owned.Select(entry => entry.Path).Distinct(StringComparer.OrdinalIgnoreCase)) {
            if (!registeredLists.Any(registered => SameAbsolutePath(path, registered))) {
                result.PreservedFiles.Add(path);
                continue;
            }

            IniDocument document;
            string[] applications;
            string? hash;
            try {
                (document, applications, hash) = ReadEditableAppList(path);
                if (hash == null)
                    continue;
            } catch (Exception error) when (error is IOException
                or InvalidDataException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or System.Security.SecurityException) {
                PreserveForRetry(path);
                continue;
            }

            string[] remaining = applications.Where(application =>
                !SameAbsolutePath(application, game.Executable))
                .ToArray();
            if (remaining.Length != applications.Length) {
                document.Set("", "Apps", IniDocument.Join(remaining));
                if (!transaction.RewriteIfUnchanged(path, hash, document.ToString()))
                    PreserveForRetry(path);
            }
        }

        state.OwnedVulkanApps.RemoveAll(entry =>
            SameAbsolutePath(entry.Executable, game.Executable) && !retryable.Contains(entry.Path));
    }

    private static bool SameAbsolutePath(string? first, string? second) {
        try {
            return first != null
                && second != null
                && Path.IsPathFullyQualified(first)
                && Path.IsPathFullyQualified(second)
                && LocalFiles.Same(first, second);
        } catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) {
            return false;
        }
    }

    private static string[] ReadAppEntries(string path) => File.Exists(path)
        ? ReadAppEntries(new IniDocument(
            ConfigurationService.ReadConfigurationText(ConfigurationService.ReadConfigurationBytes(path))))
        : [];

    private static string[] ReadAppEntries(IniDocument document) => IniDocument.Split(document.Get("", "Apps"));

    private static (IniDocument Document, string[] Applications, string? Hash) ReadEditableAppList(string path) {
        LocalFiles.CheckParents(path);
        if (LocalFiles.Link(path) != null || Directory.Exists(path))
            throw new IOException("Application list is not a regular file: " + path);

        bool exists = File.Exists(path);
        byte[] contents = exists ? ConfigurationService.ReadConfigurationBytes(path) : [];
        var document = new IniDocument(ConfigurationService.ReadConfigurationText(contents));
        string? hash = exists ? Convert.ToHexString(SHA256.HashData(contents)) : null;
        return (document, ReadAppEntries(document), hash);
    }
}
