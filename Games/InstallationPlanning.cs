using System.Security.Cryptography;
using static ReShadeCentralized.GameFiles;

namespace ReShadeCentralized;

public sealed record ExistingFile(string? LinkTarget) {
    public string? Hash { get; init; }
}

public sealed record InstallationLink(string Path, string Target, ExistingFile? Previous) {
    public bool ReplacesFile => Previous != null;
    public string? PreviousTarget => Previous?.LinkTarget;
    public string? ExpectedOwnedTarget { get; init; }
}

public sealed record InstallationPlan(
    string GameDirectory,
    string ProfileDirectory,
    IReadOnlyList<InstallationLink> Links,
    IReadOnlyList<string> RemovedLinks,
    IReadOnlyList<string> LocalSearchPaths,
    string? PresetSource,
    string Fingerprint,
    string? Conflict) {
    public string? Executable { get; init; }
    internal CpuArchitecture Architecture { get; init; }
    public string? ConfigurationSource { get; init; }
    public string? ConfigurationSourceHash { get; init; }
    public string? PresetSourceHash { get; init; }

    public bool RequiresConfirmation => Links.Any(link => link.ReplacesFile);

    public string Describe() {
        string FileLabel(string path) =>
            path.StartsWith(Path.TrimEndingDirectorySeparator(GameDirectory) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(GameDirectory, path) :
            Path.GetFileName(path);
        var lines = new List<string>();
        foreach (InstallationLink link in Links) {
            string operation = "Link";
            if (link.ReplacesFile)
                operation = link.PreviousTarget == null ? "Replace file" : "Replace link";
            string destination = LocalFiles.Same(link.Target, Path.Combine(ProfileDirectory, "ReShade.ini"))
                ? "game profile" : "central runtime";
            lines.Add($"{operation}: {FileLabel(link.Path)} -> {destination} ({FileLabel(link.Target)}).");
        }

        lines.AddRange(RemovedLinks.Select(path => "Remove managed link: " + FileLabel(path)));
        if (PresetSource != null)
            lines.Add($"Copy preset: {FileLabel(PresetSource)} -> game profile (ReShadePreset.ini).");

        lines.AddRange(LocalSearchPaths.Select(path => "Keep local shaders/textures: reshade-shaders\\" +
            Path.GetFileName(path.TrimEnd('*', '\\', '/'))));
        if (RequiresConfirmation)
            lines.Add(GameFiles.ReplacementWarning);

        if (Conflict != null) {
            string conflict = Conflict;
            string[] paths = GameFiles.InspectionPaths(GameDirectory)
                .Concat(Links.Select(link => link.Path)).Concat(RemovedLinks)
                .Distinct().OrderByDescending(path => path.Length).ToArray();
            foreach (string path in paths) {
                conflict = conflict.Replace(path, FileLabel(path), StringComparison.OrdinalIgnoreCase);
            }
            lines.Add(conflict);
        }

        return string.Join("\n\n", lines);
    }
}

public static class InstallationPlanner {
    public static InstallationPlan CreatePlan(ManagerState state, GameProfile game) {
        if (!Enum.IsDefined(game.Api) || !Enum.IsDefined(game.Build))
            throw new NotSupportedException("Select a supported graphics API and runtime build.");

        GameProfile? previous = state.Games.SingleOrDefault(item => item.Id == game.Id);
        GameDeployment.ValidateRecordedLinks(state, previous ?? game);

        string executable = PortableExecutable.GetValidatedGamePath(game.Executable, out CpuArchitecture architecture) ??
            throw new IOException("Choose an existing local x86/x64 game executable outside Windows folders.");
        string directory = Path.GetDirectoryName(executable)!;
        string config = Path.Combine(directory, "ReShade.ini");
        LocalFiles.CheckParents(config);
        if (state.Games.Any(other => other.Id != game.Id
            && LocalFiles.Same(directory, Path.GetDirectoryName(other.Executable)!))) {
            throw new IOException("Another managed profile already uses this executable directory. " +
                "Select that profile and choose Repair / edit instead.");
        }

        string profile = ProfilePaths.DirectoryFor(state, game, executable);
        LocalFiles.Contained(state.Paths.Profiles, Path.GetRelativePath(state.Paths.Profiles, profile));
        if (previous == null && (Directory.Exists(profile) || LocalFiles.Exists(profile)))
            throw new IOException("Profile destination already exists: " + profile);

        string centralConfig = Path.Combine(profile, "ReShade.ini");
        ExistingInstallationNotice? notice =
            InstallationInspection.InspectDirectory(directory, game.Api, game.Links, includePaths: true);
        string? conflict = notice?.BlocksInstallation == true ? notice.Message : null;
        var links = new List<InstallationLink>();
        var observations = new List<string> {
            executable, game.Api.ToString(), game.Build.ToString(), architecture.ToString(), profile
        };
        if (Proxy(game.Api) is string proxy) {
            AddLink(Path.Combine(directory, proxy),
                Path.Combine(state.Paths.Binaries(game.Build), RuntimeFileName(architecture)));
        }
        AddLink(config, centralConfig);

        var removedLinks = new List<string>();
        foreach (OwnedLink old in game.Links) {
            LocalFiles.CheckParents(old.Path);
            bool remains = links.Any(link => LocalFiles.Same(link.Path, old.Path));
            bool exists = LocalFiles.Exists(old.Path) || Directory.Exists(old.Path);
            bool owned = exists && IsOwnedLink(old);
            if (exists && !owned && (!remains || !CanReplaceReShadeLink(old.Path))) {
                conflict = "Managed files were changed outside this app. " +
                    "Resolve this path before installing: " + old.Path;
                continue;
            }

            if (!remains && owned)
                removedLinks.Add(old.Path);
            Observe(old.Path);
        }

        foreach (string path in InspectionPaths(directory)) {
            Observe(path);
        }

        string? preset = null;
        string? presetHash = null;
        string? sourceHash = null;
        string? source = conflict == null && !File.Exists(centralConfig) && HasFileTarget(config) ? config : null;
        if (source != null) {
            observations.Add("Configuration source: " + source);
            Observe(source);
            byte[] contents = ConfigurationService.ReadConfigurationBytes(source);
            sourceHash = Convert.ToHexString(SHA256.HashData(contents));
            observations.Add("Configuration contents: " + sourceHash);
            preset = ConfigurationService.SelectedPreset(
                new IniDocument(ConfigurationService.ReadConfigurationText(contents)),
                directory);
            if (preset != null) {
                // Opening the file here makes unreadable selections fail before replacing any game files.
                presetHash = LocalFiles.Hash(preset);
                observations.Add(preset + " preset:" + presetHash);
            }
        }

        string[] localPaths = ConfigurationService.LocalSearchPaths(game, false)
            .Concat(ConfigurationService.LocalSearchPaths(game, true)).ToArray();
        observations.AddRange(localPaths);
        observations.Add(preset ?? "");
        string fingerprint = LocalFiles.HashText(string.Join("\n", observations));
        return new(directory, profile, links, removedLinks, localPaths, preset, fingerprint, conflict) {
            Executable = executable,
            Architecture = architecture,
            ConfigurationSource = source,
            ConfigurationSourceHash = sourceHash,
            PresetSourceHash = presetHash
        };

        void AddLink(string path, string target) {
            observations.Add(path + " -> " + target);
            if (IsClaimedByAnotherProfile(state, game, path)) {
                conflict = "Another managed profile already uses this file: " + path;
                return;
            }

            OwnedLink? owned = game.Links.FirstOrDefault(link => LocalFiles.Same(link.Path, path) && IsOwnedLink(link));
            if (owned == null && LocalFiles.Link(path) != null && !CanReplaceReShadeLink(path))
                conflict = "An unmanaged symbolic link occupies this path. Resolve it before installing: " + path;

            if (Directory.Exists(path))
                conflict = "A directory occupies a required file path. Resolve it before installing: " + path;

            string? previousTarget = LocalFiles.Link(path);
            ExistingFile? previous = LocalFiles.Exists(path) && owned == null
                ? new(previousTarget) { Hash = previousTarget == null ? LocalFiles.Hash(path) : null }
                : null;
            observations.Add("Prior file: " + (owned?.Target ?? previousTarget ?? previous?.Hash ?? "missing"));
            links.Add(new(path, target, previous) { ExpectedOwnedTarget = owned?.Target });
        }

        void Observe(string path) {
            string? link = LocalFiles.Link(path);
            string contents = HasFileTarget(path) ? LocalFiles.Hash(path) : "missing";
            observations.Add(path + "=" + (link != null ? "link:" + link + ":" : "") + contents);
        }
    }

    public static void ValidatePlan(InstallationPlan current, InstallationPlan? reviewed, bool replaceExisting) {
        if (current.Conflict != null)
            throw new IOException(current.Conflict);

        if ((reviewed != null && current.Fingerprint != reviewed.Fingerprint)
            || (current.RequiresConfirmation && (reviewed == null || !replaceExisting)))
            throw new IOException("Review the installation changes again and confirm replacement of existing files.");
    }

}
