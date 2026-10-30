using System.Security.Cryptography;
using System.Text;
using static ReShadeCentralized.GameFiles;

namespace ReShadeCentralized;

public static class InstallationPlanner
{
    public static InstallationPlan CreatePlan(ManagerState state, GameProfile game)
    {
        if (!Enum.IsDefined(game.Api) || !Enum.IsDefined(game.Build))
        {
            throw new NotSupportedException("Select a supported graphics API and runtime build.");
        }
        if (string.IsNullOrWhiteSpace(game.Executable) || !File.Exists(game.Executable))
        {
            throw new FileNotFoundException("The game executable is missing. " +
                "Open Games > Install game or Repair / edit and select its Executable.");
        }

        string selected = PortableExecutable.GetValidatedGamePath(game.Executable) ??
            throw new IOException("Choose a valid local x86/x64 game executable outside Windows folders.");
        GameTarget target = GameTargetResolver.Resolve(selected);
        string executable = target.Executable;
        string directory = CompatibilityCatalog.Default.InstallationDirectory(executable, game.Api);
        LocalFiles.CheckParents(Path.Combine(directory, "ReShade.ini"));
        if (!Directory.Exists(directory))
        {
            throw new IOException("The compatibility install folder is missing: " + directory +
                ". Select the actual game executable or restore this game folder before installing.");
        }
        string profile = game.Directory.Length == 0 ?
            ProfilePaths.DirectoryFor(state, game, executable) : game.Directory;
        string centralConfig = Path.Combine(profile, "ReShade.ini");
        ExistingInstallationNotice? notice =
            InstallationInspection.InspectExistingInstallation(executable, game.Api, game.Links);
        string? conflict = notice?.BlocksInstallation == true ? notice.Message : null;
        var links = new List<InstallationLink>();
        var observations = new List<string> { executable, game.Api.ToString(), game.Build.ToString(), profile };
        string? proxy = CompatibilityCatalog.Default.Proxy(executable, game.Api);
        CpuArchitecture architecture = CompatibilityCatalog.Default.Architecture(executable, game.Api);
        string stem = architecture == CpuArchitecture.X64 ?
            "ReShade64.dll" : "ReShade32.dll";
        if (proxy != null)
        {
            AddLink(Path.Combine(directory, proxy), Path.Combine(state.Paths.Binaries(game.Build), stem));
        }

        foreach (string config in ConfigurationPaths(executable, game.Api))
        {
            AddLink(config, centralConfig);
        }
        string[] removedLinks = game.Links.Where(old => !links.Any(link => LocalFiles.Same(link.Path, old.Path)))
            .Select(link => link.Path).ToArray();
        foreach (OwnedLink old in game.Links)
        {
            Observe(old.Path);
            if (LocalFiles.Exists(old.Path) && !IsOwnedLink(old) && !CanReplaceReShadeLink(old.Path))
            {
                conflict = "Managed files were changed outside this app. " +
                    "Resolve this path before installing: " + old.Path;
            }
        }

        foreach (string path in InspectionPaths(executable, game.Api))
        {
            Observe(path);
        }

        string? preset = null;
        bool adopting = !File.Exists(centralConfig);
        string? source = adopting ? ExistingConfiguration(executable, game.Api) : null;
        if (source != null)
        {
            observations.Add("Configuration source: " + source);
            Observe(source);
            preset = ConfigurationService.SelectedPreset(ReadAdoptedConfiguration(source), Path.GetDirectoryName(source)!);
            if (preset != null)
            {
                // Opening the file here makes unreadable selections fail before replacing any game files.
                observations.Add(preset + " preset:" + LocalFiles.Hash(preset));
            }
        }

        string[] localPaths = ConfigurationService.LocalSearchPaths(game, false)
            .Concat(ConfigurationService.LocalSearchPaths(game, true)).ToArray();
        observations.AddRange(localPaths);
        observations.Add(preset ?? "");
        byte[] fingerprintBytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", observations)));
        string fingerprint = Convert.ToHexString(fingerprintBytes);
        return new(directory, profile, links, removedLinks, localPaths, preset, fingerprint, conflict)
        {
            Executable = executable,
            TargetReason = target.Reason,
            ConfigurationSource = source,
            RuntimeArchitecture = architecture != PortableExecutable.Architecture(executable) ? architecture : null
        };

        void AddLink(string path, string target)
        {
            observations.Add(path + " -> " + target);
            bool owned = game.Links.Any(link => LocalFiles.Same(link.Path, path) && IsOwnedLink(link));
            if (!owned && LocalFiles.Link(path) != null && !CanReplaceReShadeLink(path))
            {
                conflict = "An unmanaged symbolic link occupies this path. Resolve it before installing: " + path;
            }

            if (Directory.Exists(path))
            {
                conflict = "A directory occupies a required file path. Resolve it before installing: " + path;
            }

            ExistingFile? previous = LocalFiles.Exists(path) && !owned
                ? new(LocalFiles.Link(path)) : null;
            links.Add(new(path, target, previous));
        }

        void Observe(string path)
        {
            string? link = LocalFiles.Link(path);
            string contents = HasFileTarget(path) ? LocalFiles.Hash(path) : "missing";
            observations.Add(path + "=" + (link != null ? "link:" + link + ":" : "") + contents);
        }
    }

    public static void ValidatePlan(InstallationPlan current, InstallationPlan? reviewed, bool replaceExisting)
    {
        if (current.Conflict != null)
        {
            throw new IOException(current.Conflict);
        }

        if ((reviewed != null && current.Fingerprint != reviewed.Fingerprint) ||
            (current.RequiresConfirmation && (reviewed == null || !replaceExisting)))
        {
            throw new IOException("Review the installation changes again and confirm replacement of existing files.");
        }
    }

}
