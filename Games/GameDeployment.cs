using static ReShadeCentralized.GameFiles;

namespace ReShadeCentralized;

public static class GameDeployment
{
    private static void Link(
        GameProfile game,
        string path,
        string target,
        FileTransaction transaction,
        bool adoptConfig = false)
    {
        OwnedLink? prior = game.Links.SingleOrDefault(link => LocalFiles.Same(link.Path, path));
        if (prior != null && LocalFiles.Exists(path) && !IsOwnedLink(prior) && !CanReplaceReShadeLink(path))
        {
            throw new IOException("Managed link was replaced externally; preserving it: " + path);
        }

        if (prior == null && LocalFiles.Exists(path) && !adoptConfig && !IsReShadeHook(path))
        {
            throw new IOException("Refusing to replace an unrelated file: " + path);
        }

        transaction.Symlink(path, target);
        game.Links.RemoveAll(link => LocalFiles.Same(link.Path, path));
        game.Links.Add(new(path, target));
    }

    public static void Install(
        ManagerState state,
        GameProfile game,
        FileTransaction transaction,
        InstallationPlan? reviewedPlan = null,
        bool replaceExisting = false)
    {
        InstallationPlan plan = InstallationPlanner.CreatePlan(state, game);
        InstallationPlanner.ValidatePlan(plan, reviewedPlan, replaceExisting);
        game.Executable = plan.Executable!;
        game.Architecture = CompatibilityCatalog.Default.Architecture(game.Executable, game.Api);
        string gameDirectory = CompatibilityCatalog.Default.InstallationDirectory(game.Executable, game.Api);
        string stem = game.Architecture == CpuArchitecture.X64 ? "ReShade64" : "ReShade32";
        string dll = Path.Combine(state.Paths.Binaries(game.Build), stem + ".dll");
        if (!PortableExecutable.IsReShade(dll) || PortableExecutable.Architecture(dll) != game.Architecture)
        {
            throw new IOException($"The {DisplayNames.For(game.Build)} runtime for {game.Architecture} " +
                "is missing or invalid. " +
                "Open Runtime & storage > Update ReShade, then retry Games > Install game or Repair / edit.");
        }

        string? proxy = CompatibilityCatalog.Default.Proxy(game.Executable, game.Api);
        string centralConfig = ConfigurationService.ConfigPath(game);
        string? initial = plan.ConfigurationSource != null ?
            ReadAdoptedConfiguration(plan.ConfigurationSource).ToString() : null;
        if (initial != null && plan.PresetSource != null)
        {
            transaction.Copy(plan.PresetSource, Path.Combine(game.Directory, "ReShadePreset.ini"));
        }

        ConfigurationService.Update(state, game, transaction, initial);
        foreach (OwnedLink old in game.Links.ToArray())
        {
            if (plan.Links.Any(link => LocalFiles.Same(link.Path, old.Path)))
            {
                continue;
            }

            if (LocalFiles.Exists(old.Path) && !IsOwnedLink(old))
            {
                throw new IOException("Cannot remove a replaced managed link: " + old.Path);
            }

            transaction.Delete(old.Path);
            game.Links.Remove(old);
        }

        if (proxy != null)
        {
            Link(game, Path.Combine(gameDirectory, proxy), dll, transaction);
        }

        foreach (string config in ConfigurationPaths(game.Executable, game.Api))
        {
            Link(game, config, centralConfig, transaction, true);
        }
    }

    public static UnregisterResult Remove(GameProfile game, FileTransaction transaction)
    {
        var result = new UnregisterResult();
        foreach (OwnedLink link in game.Links)
        {
            if (!LocalFiles.Exists(link.Path) && !Directory.Exists(link.Path))
            {
                continue;
            }

            if (!IsOwnedLink(link))
            {
                result.PreservedFiles.Add(link.Path);
                continue;
            }

            transaction.Delete(link.Path);
        }

        return result;
    }
}
