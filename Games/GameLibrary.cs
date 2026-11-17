using System.Security.Cryptography;
using static ReShadeCentralized.GameFiles;

namespace ReShadeCentralized;

public sealed class GameLibrary(CentralStorage storage, VulkanService vulkan) {
    public Task InstallAsync(
        GameProfile request,
        CancellationToken cancellationToken,
        InstallationPlan? reviewedPlan = null,
        bool replaceExisting = false) {
        string displayName = string.IsNullOrWhiteSpace(request.Executable) ? request.Name :
            Path.GetFileName(request.Executable);
        return storage.ChangeAsync("Install / repair " + displayName, (updatedState, transaction) => {
            GameProfile? existing = updatedState.Games.SingleOrDefault(gameItem => gameItem.Id == request.Id);
            GameProfile game = request.Clone();
            if (existing != null) {
                game.Links = existing.Links;
                game.Directory = existing.Directory;
            } else {
                game.Directory = "";
                game.Links = [];
            }

            if (game.Api == RenderApi.Vulkan)
                game.Build = updatedState.VulkanBuild;

            GameDeployment.Install(updatedState, game, transaction, reviewedPlan, replaceExisting);
            updatedState.Games.RemoveAll(gameItem => gameItem.Id == game.Id);
            updatedState.Games.Add(game);
            if (game.Api == RenderApi.Vulkan)
                vulkan.Enable(updatedState, transaction, game, existing?.Api == RenderApi.Vulkan ? existing.Executable : null);
            else if (updatedState.VulkanManaged && existing?.Api == RenderApi.Vulkan)
                vulkan.WriteApps(updatedState, transaction, enroll: false);

            return Task.CompletedTask;
        }, cancellationToken);
    }

    public async Task<UnregisterResult> UnregisterAsync(string id, CancellationToken cancellationToken) {
        UnregisterResult? result = null;
        await storage.ChangeAsync("Unregister game", (updatedState, transaction) => {
            GameProfile game = updatedState.Games.Single(gameItem => gameItem.Id == id);
            result = GameRemoval.Remove(updatedState, game, transaction, cancellationToken);
            updatedState.Games.Remove(game);
            vulkan.UnregisterGame(updatedState, game, transaction, result);

            return Task.CompletedTask;
        }, cancellationToken);
        return result!;
    }

}

public static class GameDeployment {
    private static void Link(
        GameProfile game,
        InstallationLink link,
        FileTransaction transaction) {
        string? expectedTarget = link.ExpectedOwnedTarget;
        if (expectedTarget == null && link.PreviousTarget != null)
            expectedTarget = Path.GetFullPath(link.PreviousTarget, Path.GetDirectoryName(link.Path)!);

        string? expectedHash = link.Previous?.Hash;
        if ((expectedTarget != null || expectedHash != null)
            && !transaction.DeleteIfUnchanged(link.Path, expectedTarget, expectedHash))
            throw new IOException("A reviewed game file changed; preserving it: " + link.Path);

        transaction.SymlinkIfAbsent(link.Path, link.Target);
        game.Links.RemoveAll(owned => LocalFiles.Same(owned.Path, link.Path));
        game.Links.Add(new(link.Path, link.Target));
    }

    public static void Install(
        ManagerState state,
        GameProfile game,
        FileTransaction transaction,
        InstallationPlan? reviewedPlan = null,
        bool replaceExisting = false) {
        InstallationPlan plan = InstallationPlanner.CreatePlan(state, game);
        InstallationPlanner.ValidatePlan(plan, reviewedPlan, replaceExisting);
        game.Executable = plan.Executable!;
        game.Name = Path.GetFileName(game.Executable);
        game.Directory = plan.ProfileDirectory;
        game.Architecture = plan.Architecture;
        string dll = Path.Combine(state.Paths.Binaries(game.Build), RuntimeFileName(game.Architecture));
        if (!PortableExecutable.IsReShade(dll) || PortableExecutable.Architecture(dll) != game.Architecture) {
            throw new IOException($"The {DisplayNames.For(game.Build)} runtime for {game.Architecture} " +
                "is missing or invalid. " +
                "Open Runtime & storage > Update ReShade, then retry Games > Install game or Repair / edit.");
        }

        string? initial = null;
        if (plan.ConfigurationSource != null) {
            byte[] configuration = ConfigurationService.ReadConfigurationBytes(plan.ConfigurationSource);
            if (Convert.ToHexString(SHA256.HashData(configuration)) != plan.ConfigurationSourceHash)
                throw new IOException("The selected configuration changed; review installation again.");

            initial = ConfigurationService.ReadConfigurationText(configuration);
        }
        if (initial != null && plan.PresetSource != null) {
            byte[] preset = ConfigurationService.ReadConfigurationBytes(plan.PresetSource);
            if (Convert.ToHexString(SHA256.HashData(preset)) != plan.PresetSourceHash)
                throw new IOException("The selected preset changed; review installation again.");

            if (!transaction.WriteBytesIfAbsent(Path.Combine(game.Directory, "ReShadePreset.ini"), preset)) {
                throw new IOException("The profile already has a preset; preserving it. " +
                    "Review the existing profile before importing another preset.");
            }
        }

        ConfigurationService.Update(state, game, transaction, initial);
        foreach (OwnedLink old in game.Links.ToArray()) {
            if (plan.Links.Any(link => LocalFiles.Same(link.Path, old.Path)))
                continue;

            if ((LocalFiles.Exists(old.Path) || Directory.Exists(old.Path))
                && !transaction.DeleteIfUnchanged(old.Path, expectedTarget: old.Target))
                throw new IOException("Cannot remove a replaced managed link: " + old.Path);

            game.Links.Remove(old);
        }

        foreach (InstallationLink link in plan.Links) {
            Link(game, link, transaction);
        }
    }

    internal static void ValidateRecordedLinks(ManagerState state, GameProfile game) {
        if (game.Links.Count == 0)
            return;

        if (!Path.IsPathFullyQualified(game.Executable) || !Path.IsPathFullyQualified(game.Directory)
            || !Enum.IsDefined(game.Api) || !Enum.IsDefined(game.Build) || !Enum.IsDefined(game.Architecture))
            throw new InvalidDataException("Cannot verify the game's recorded installation links.");

        LocalFiles.Contained(state.Paths.Profiles, Path.GetRelativePath(state.Paths.Profiles, game.Directory));
        foreach (OwnedLink link in game.Links) {
            if (!IsInstallationLink(state.Paths, game, link) || IsClaimedByAnotherProfile(state, game, link.Path))
                throw new IOException("Cannot verify ownership of a recorded game link; preserving it: " + link.Path);
        }
    }

}

public sealed class UnregisterResult {
    public List<string> PreservedFiles { get; } = [];
}

public static class GameRemoval {
    public static UnregisterResult Remove(
        ManagerState state,
        GameProfile game,
        FileTransaction transaction,
        CancellationToken cancellationToken = default) {
        ValidateProfile(state, game);
        var result = new UnregisterResult();
        var presets = Presets(state, game, result);

        foreach (OwnedLink link in game.Links.Concat(GameFiles.ExpectedLinks(state.Paths, game))
            .DistinctBy(link => link.Path, StringComparer.OrdinalIgnoreCase)) {
            cancellationToken.ThrowIfCancellationRequested();
            try {
                if (!Path.IsPathFullyQualified(link.Path) || !Path.IsPathFullyQualified(link.Target)) {
                    Preserve(link.Path);
                    continue;
                }

                LocalFiles.CheckParents(link.Path);
                if (!LocalFiles.Exists(link.Path) && !Directory.Exists(link.Path))
                    continue;

                bool recorded = game.Links.Any(owned => LocalFiles.Same(owned.Path, link.Path)
                    && LocalFiles.Same(owned.Target, link.Target));
                if (!recorded || !GameFiles.IsInstallationLink(state.Paths, game, link)
                    || GameFiles.IsClaimedByAnotherProfile(state, game, link.Path)
                    || !transaction.DeleteIfUnchanged(link.Path, expectedTarget: link.Target))
                    Preserve(link.Path);
            } catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or System.Security.SecurityException) {
                throw new IOException("Cannot safely remove managed link: " + link.Path, error);
            }
        }

        foreach ((string path, string hash) in presets) {
            cancellationToken.ThrowIfCancellationRequested();
            if (!transaction.DeleteIfUnchanged(path, expectedHash: hash))
                Preserve(path);
        }

        return result;

        void Preserve(string path) {
            if (!result.PreservedFiles.Contains(path, StringComparer.OrdinalIgnoreCase))
                result.PreservedFiles.Add(path);
        }
    }

    private static void ValidateProfile(ManagerState state, GameProfile game) {
        if (!Path.IsPathFullyQualified(state.Paths.Root)
            || !Path.IsPathFullyQualified(game.Directory)
            || !Path.IsPathFullyQualified(game.Executable)
            || !Inside(state.Paths.Profiles, game.Directory)
            || !Enum.IsDefined(game.Api)
            || !Enum.IsDefined(game.Build)
            || !Enum.IsDefined(game.Architecture)) {
            throw new InvalidDataException(
                "Cannot safely unregister: the game's storage or installation path is invalid.");
        }

        string relative = Path.GetRelativePath(state.Paths.Profiles, game.Directory);
        if (relative.Split(['\\', '/']).Any(part => part.EndsWith('.')
            || part.EndsWith(' ')
            || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("Cannot safely unregister: the profile path is ambiguous.");

        LocalFiles.CheckParents(Path.Combine(game.Directory, "check"));
        if (state.Games.Any(other => other.Id != game.Id
            && (LocalFiles.Same(other.Directory, game.Directory)
                || Inside(game.Directory, other.Directory)
                || Inside(other.Directory, game.Directory))))
            throw new InvalidDataException("Cannot safely unregister: another game shares this profile folder.");
    }

    private static List<(string Path, string Hash)> Presets(
        ManagerState state,
        GameProfile game,
        UnregisterResult result) {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            Path.Combine(game.Directory, "ReShadePreset.ini")
        };

        string config = ConfigurationService.ConfigPath(game);
        if (LocalFiles.Link(config) != null || Directory.Exists(config))
            result.PreservedFiles.Add(config);
        else if (File.Exists(config)) {
            try {
                var document = new IniDocument(ConfigurationService.ReadConfigurationText(
                    ConfigurationService.ReadConfigurationBytes(config)));
                string? selected = ConfigurationService.SelectedPreset(document, game.Directory);
                if (selected != null)
                    candidates.Add(selected);
            } catch (InvalidDataException) {
                result.PreservedFiles.Add(config);
            }
        }

        var presets = new List<(string, string)>();
        foreach (string path in candidates) {
            if (!Inside(game.Directory, path) || LocalFiles.Same(path, config)) {
                result.PreservedFiles.Add(path);
                continue;
            }

            LocalFiles.CheckParents(path);
            if (LocalFiles.Link(path) != null || Directory.Exists(path)) {
                result.PreservedFiles.Add(path);
                continue;
            }

            if (!File.Exists(path))
                continue;

            string extension = Path.GetExtension(path);
            if (!(extension.Equals(".ini", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))) {
                result.PreservedFiles.Add(path);
                continue;
            }

            byte[] contents;
            IniDocument preset;
            try {
                contents = ConfigurationService.ReadConfigurationBytes(path);
                preset = new IniDocument(ConfigurationService.ReadConfigurationText(contents));
            } catch (InvalidDataException) {
                result.PreservedFiles.Add(path);
                continue;
            }
            if ((preset.Get("", "Techniques") == null && preset.Get("", "TechniqueSorting") == null)
                || ReferencedByAnotherGame(state, game, path)) {
                result.PreservedFiles.Add(path);
                continue;
            }

            presets.Add((path, Convert.ToHexString(SHA256.HashData(contents))));
        }

        return presets;
    }

    private static bool ReferencedByAnotherGame(ManagerState state, GameProfile game, string preset) {
        foreach (GameProfile other in state.Games.Where(other => other.Id != game.Id)) {
            string config = ConfigurationService.ConfigPath(other);
            LocalFiles.CheckParents(config);
            if (LocalFiles.Link(config) != null || Directory.Exists(config) || !File.Exists(config))
                return true;

            string? selected;
            try {
                var document = new IniDocument(ConfigurationService.ReadConfigurationText(
                    ConfigurationService.ReadConfigurationBytes(config)));
                selected = ConfigurationService.SelectedPreset(document, other.Directory);
            } catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException or System.Security.SecurityException) {
                return true;
            }
            if (selected != null && LocalFiles.Same(selected, preset))
                return true;
        }

        return false;
    }

    private static bool Inside(string directory, string path) => Path.GetFullPath(path).StartsWith(
        Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase);
}
