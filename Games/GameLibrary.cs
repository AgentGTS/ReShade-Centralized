namespace ReShadeCentralized;

public sealed class GameLibrary(CentralStorage storage, VulkanService vulkan)
{
    public Task InstallAsync(
        GameProfile request,
        CancellationToken cancellationToken,
        InstallationPlan? reviewedPlan = null,
        bool replaceExisting = false)
    {
        string displayName = string.IsNullOrWhiteSpace(request.Executable) ? request.Name :
            Path.GetFileName(request.Executable);
        return storage.ChangeAsync("Install / repair " + displayName, (updatedState, transaction) =>
        {
            GameProfile? existing = updatedState.Games.SingleOrDefault(gameItem => gameItem.Id == request.Id);
            GameProfile game = request.Clone();
            if (existing != null)
            {
                game.Links = existing.Links;
                game.Directory = existing.Directory;
            }
            else
            {
                game.Directory = "";
            }

            if (PortableExecutable.GetValidatedGamePath(game.Executable) == null)
            {
                throw new FileNotFoundException("Open Games > Install game or Repair / edit and select " +
                    "the game's actual local executable.");
            }
            game.Executable = GameTargetResolver.Resolve(game.Executable, cancellationToken).Executable;
            game.Name = Path.GetFileName(game.Executable);
            string[] configurationPaths = GameFiles.ConfigurationPaths(game.Executable, game.Api);
            if (updatedState.Games.Any(gameItem => gameItem.Id != game.Id &&
                GameFiles.ConfigurationPaths(gameItem.Executable, gameItem.Api)
                    .Any(path => configurationPaths.Any(candidate => LocalFiles.Same(path, candidate)))))
            {
                throw new IOException("Another managed profile already uses this executable directory. " +
                    "Select that profile and choose Repair / edit instead.");
            }

            if (game.Api == RenderApi.Vulkan)
            {
                game.Build = updatedState.VulkanBuild;
            }

            game.Directory = ProfilePaths.DirectoryFor(updatedState, game);
            if (existing == null && (Directory.Exists(game.Directory) || LocalFiles.Exists(game.Directory)))
            {
                throw new IOException("Profile destination already exists: " + game.Directory);
            }

            updatedState.Games.RemoveAll(gameItem => gameItem.Id == game.Id);
            updatedState.Games.Add(game);
            GameDeployment.Install(updatedState, game, transaction, reviewedPlan, replaceExisting);
            if (game.Api == RenderApi.Vulkan)
            {
                vulkan.Enable(updatedState, transaction);
            }
            else if (updatedState.VulkanManaged)
            {
                vulkan.WriteApps(updatedState, transaction, enroll: false);
            }

            return Task.CompletedTask;
        }, cancellationToken);
    }

    public async Task<UnregisterResult> UnregisterAsync(string id, CancellationToken cancellationToken)
    {
        UnregisterResult? result = null;
        await storage.ChangeAsync("Unregister game", (updatedState, transaction) =>
        {
            GameProfile game = updatedState.Games.Single(gameItem => gameItem.Id == id);
            result = GameDeployment.Remove(game, transaction);
            updatedState.Games.Remove(game);
            if (updatedState.VulkanManaged)
            {
                vulkan.WriteApps(updatedState, transaction, enroll: false);
            }

            return Task.CompletedTask;
        }, cancellationToken);
        return result!;
    }

    public Task RepairAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        return storage.ChangeAsync("Repair managed links and configuration", (updatedState, transaction) =>
        {
            foreach (GameProfile game in updatedState.Games)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report("Repairing " + game.Name);
                GameDeployment.Install(updatedState, game, transaction);
            }

            if (updatedState.VulkanManaged)
            {
                vulkan.Enable(updatedState, transaction);
            }

            return Task.CompletedTask;
        }, cancellationToken);
    }

}
