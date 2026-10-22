namespace ReShadeCentralized;

public sealed class DeploymentService
{
    public static readonly string[] ProxyNames =
        ["d3d9.dll", "d3d10.dll", "d3d11.dll", "d3d12.dll", "dxgi.dll", "opengl32.dll"];

    public static string? Proxy(RenderApi api) => api switch
    {
        RenderApi.Direct3D9 => "d3d9.dll",
        RenderApi.DXGI => "dxgi.dll",
        RenderApi.OpenGL => "opengl32.dll",
        _ => null
    };

    public static bool IsOwnedLink(OwnedLink link)
    {
        string? target = FileTransaction.Link(link.Path);
        return target != null &&
            FileTransaction.Same(
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(link.Path)!, target)),
            link.Target);
    }

    private static void Link(
        GameProfile game,
        string path,
        string target,
        FileTransaction transaction,
        bool adoptConfig = false)
    {
        OwnedLink? prior = game.Links.SingleOrDefault(link => FileTransaction.Same(link.Path, path));
        if (prior != null && FileTransaction.Exists(path) && !IsOwnedLink(prior))
        {
            throw new IOException("Managed link was replaced externally; preserving it: " + path);
        }

        if (prior == null && FileTransaction.Exists(path) && !adoptConfig && !PortableExecutable.IsReShade(path))
        {
            throw new IOException("Refusing to replace an unrelated file: " + path);
        }

        transaction.Symlink(path, target);
        game.Links.RemoveAll(link => FileTransaction.Same(link.Path, path));
        game.Links.Add(new(path, target));
    }

    public static void Install(
        ManagerState state,
        GameProfile game,
        FileTransaction transaction,
        ManagerState? previous = null)
    {
        if (!Enum.IsDefined(game.Api))
        {
            throw new NotSupportedException("Unsupported rendering API.");
        }

        if (game.Executable == null || !File.Exists(game.Executable))
        {
            throw new FileNotFoundException("Select the actual game executable before linking this profile.");
        }

        if (!Path.GetExtension(game.Executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Select a Windows .exe file.");
        }

        game.Architecture = PortableExecutable.Architecture(game.Executable);
        string gameDirectory = Path.GetDirectoryName(game.Executable)!;
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (windows.Length > 0 &&
            (FileTransaction.Same(gameDirectory, windows) ||
            gameDirectory.StartsWith(windows.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
        {
            throw new IOException("Installing into Windows system directories is prohibited.");
        }

        string stem = game.Architecture == CpuArchitecture.X64 ? "ReShade64" : "ReShade32";
        string dll = Path.Combine(state.Paths.Binaries(game.Build), stem + ".dll");
        if (!PortableExecutable.IsReShade(dll) || PortableExecutable.Architecture(dll) != game.Architecture)
        {
            throw new IOException("Update the central ReShade runtime before installing this game.");
        }

        string? proxy = Proxy(game.Api);
        foreach (string name in ProxyNames)
        {
            string candidate = Path.Combine(gameDirectory, name);
            if (name != proxy && PortableExecutable.IsReShade(candidate) &&
                !game.Links.Any(link => FileTransaction.Same(link.Path, candidate) && IsOwnedLink(link)))
            {
                throw new IOException("An existing ReShade installation for another API must be removed first: " +
                    candidate);
            }
        }

        string localConfig = Path.Combine(gameDirectory, "ReShade.ini");
        string centralConfig = ConfigurationService.ConfigPath(game);
        string? initial = !File.Exists(centralConfig) &&
            File.Exists(localConfig) ?
            File.ReadAllText(localConfig) :
            null;
        // Old versions had no link inventory. Adopt only links already pointing at this profile.
        if (game.Links.Count == 0 && FileTransaction.Link(localConfig) != null &&
            FileTransaction.Same(
            new FileInfo(localConfig).ResolveLinkTarget(true)?.FullName ?? localConfig,
            centralConfig))
        {
            game.Links.Add(new(localConfig, centralConfig));
        }

        ConfigurationService.Update(state, game, transaction, previous, initial);
        foreach (OwnedLink old in game.Links.ToArray())
        {
            if (FileTransaction.Same(old.Path, localConfig) ||
                (proxy != null && FileTransaction.Same(old.Path, Path.Combine(gameDirectory, proxy))))
            {
                continue;
            }

            if (FileTransaction.Exists(old.Path) && !IsOwnedLink(old))
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

        Link(game, localConfig, centralConfig, transaction, true);
    }

    public static void Remove(GameProfile game, FileTransaction transaction)
    {
        foreach (OwnedLink link in game.Links)
        {
            if (!FileTransaction.Exists(link.Path))
            {
                continue;
            }

            if (!IsOwnedLink(link))
            {
                throw new IOException("A managed link was replaced externally; preserving it: " + link.Path);
            }

            transaction.Delete(link.Path);
        }
    }
}
