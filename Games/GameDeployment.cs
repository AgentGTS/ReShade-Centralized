using System.Security.Cryptography;
using static ReShadeCentralized.GameFiles;

namespace ReShadeCentralized;

public static class GameDeployment
{
    private static void Link(
        GameProfile game,
        InstallationLink link,
        FileTransaction transaction)
    {
        string? expectedTarget = link.ExpectedOwnedTarget;
        if (expectedTarget == null && link.PreviousTarget != null)
        {
            expectedTarget = Path.GetFullPath(link.PreviousTarget, Path.GetDirectoryName(link.Path)!);
        }

        string? expectedHash = link.Previous?.Hash;
        if ((expectedTarget != null || expectedHash != null)
            && !transaction.DeleteIfUnchanged(link.Path, expectedTarget, expectedHash))
        {
            throw new IOException("A reviewed game file changed; preserving it: " + link.Path);
        }

        transaction.SymlinkIfAbsent(link.Path, link.Target);
        game.Links.RemoveAll(owned => LocalFiles.Same(owned.Path, link.Path));
        game.Links.Add(new(link.Path, link.Target));
    }

    public static void Install(
        ManagerState state,
        GameProfile game,
        FileTransaction transaction,
        InstallationPlan? reviewedPlan = null,
        bool replaceExisting = false,
        GameProfile? previous = null)
    {
        ValidateRecordedLinks(state, previous ?? game);
        InstallationPlan plan = InstallationPlanner.CreatePlan(state, game);
        InstallationPlanner.ValidatePlan(plan, reviewedPlan, replaceExisting);
        game.Executable = plan.Executable!;
        game.Architecture = CompatibilityCatalog.Default.Architecture(game.Executable, game.Api);
        string stem = game.Architecture == CpuArchitecture.X64 ? "ReShade64" : "ReShade32";
        string dll = Path.Combine(state.Paths.Binaries(game.Build), stem + ".dll");
        if (!PortableExecutable.IsReShade(dll) || PortableExecutable.Architecture(dll) != game.Architecture)
        {
            throw new IOException($"The {DisplayNames.For(game.Build)} runtime for {game.Architecture} " +
                "is missing or invalid. " +
                "Open Runtime & storage > Update ReShade, then retry Games > Install game or Repair / edit.");
        }

        string? initial = null;
        if (plan.ConfigurationSource != null)
        {
            byte[] configuration = ConfigurationService.ReadConfigurationBytes(plan.ConfigurationSource);
            if (Convert.ToHexString(SHA256.HashData(configuration)) != plan.ConfigurationSourceHash)
            {
                throw new IOException("The selected configuration changed; review installation again.");
            }

            initial = ReadAdoptedConfiguration(plan.ConfigurationSource, configuration).ToString();
        }
        if (initial != null && plan.PresetSource != null)
        {
            byte[] preset = ConfigurationService.ReadConfigurationBytes(plan.PresetSource);
            if (Convert.ToHexString(SHA256.HashData(preset)) != plan.PresetSourceHash)
            {
                throw new IOException("The selected preset changed; review installation again.");
            }

            if (!transaction.WriteBytesIfAbsent(Path.Combine(game.Directory, "ReShadePreset.ini"), preset))
            {
                throw new IOException("The profile already has a preset; preserving it. " +
                    "Review the existing profile before importing another preset.");
            }
        }

        ConfigurationService.Update(state, game, transaction, initial);
        foreach (OwnedLink old in game.Links.ToArray())
        {
            if (plan.Links.Any(link => LocalFiles.Same(link.Path, old.Path)))
            {
                continue;
            }

            if ((LocalFiles.Exists(old.Path) || Directory.Exists(old.Path))
                && !transaction.DeleteIfUnchanged(old.Path, expectedTarget: old.Target))
            {
                throw new IOException("Cannot remove a replaced managed link: " + old.Path);
            }

            game.Links.Remove(old);
        }

        foreach (InstallationLink link in plan.Links)
        {
            Link(game, link, transaction);
        }
    }

    internal static void ValidateRecordedLinks(ManagerState state, GameProfile game)
    {
        if (game.Links.Count == 0)
        {
            return;
        }

        if (!Path.IsPathFullyQualified(game.Executable) || !Path.IsPathFullyQualified(game.Directory)
            || !Enum.IsDefined(game.Api) || !Enum.IsDefined(game.Build) || !Enum.IsDefined(game.Architecture))
        {
            throw new InvalidDataException("Cannot verify the game's recorded installation links.");
        }

        List<OwnedLink> expected = GameRemoval.ExpectedLinks(state, game);
        foreach (OwnedLink link in game.Links)
        {
            bool allowed = Path.IsPathFullyQualified(link.Path) && Path.IsPathFullyQualified(link.Target)
                && expected.Any(item => LocalFiles.Same(item.Path, link.Path)
                    && LocalFiles.Same(item.Target, link.Target));
            bool shared = state.Games.Any(other => other.Id != game.Id
                && other.Links.Any(item => LocalFiles.Same(item.Path, link.Path)));
            if (!allowed || shared)
            {
                throw new IOException("Cannot verify ownership of a recorded game link; preserving it: " + link.Path);
            }
        }
    }

}
