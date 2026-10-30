namespace ReShadeCentralized;

public sealed partial class VulkanService
{
    public Task SetVulkanBuildAsync(CentralStorage storage, RuntimeBuild build, CancellationToken cancellationToken)
    {
        return storage.ChangeAsync("Switch global Vulkan build to " + DisplayNames.For(build), (updatedState, transaction) =>
        {
            updatedState.VulkanBuild = build;
            foreach (GameProfile game in updatedState.Games.Where(game => game.Api == RenderApi.Vulkan))
            {
                game.Build = build;
            }

            Enable(updatedState, transaction);
            return Task.CompletedTask;
        }, cancellationToken);
    }

    public async Task<VulkanRemovalResult> RemoveVulkanSupportAsync(CentralStorage storage, CancellationToken cancellationToken)
    {
        VulkanRemovalResult? result = null;
        await storage.ChangeAsync("Remove app-owned Vulkan layer registrations", (updatedState, transaction) =>
        {
            result = RemoveOwnedRegistrations(updatedState, transaction);
            return Task.CompletedTask;
        }, cancellationToken);
        return result!;
    }

}
