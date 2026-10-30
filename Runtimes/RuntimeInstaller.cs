namespace ReShadeCentralized;

public sealed class RuntimeInstaller(CentralStorage storage, VulkanService vulkan, Downloads downloads)
{
    public Task UpdateRuntimeAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        return storage.ChangeAsync("Update official ReShade runtimes", async (updatedState, transaction) =>
        {
            using var staging = new StagingDirectory(storage.Store);
            await new RuntimeService(downloads).StageAsync(staging.Path, progress, cancellationToken);
            progress?.Report("Validating and deploying both runtime builds\u2026");
            RuntimeService.Deploy(staging.Path, updatedState, transaction, cancellationToken);
            if (updatedState.VulkanManaged)
            {
                vulkan.Enable(updatedState, transaction);
            }
        }, cancellationToken);
    }

    public Task ImportRuntimeAsync(
        string standardInstaller,
        string addonInstaller,
        CancellationToken cancellationToken)
    {
        return storage.ChangeAsync("Import official ReShade installers", (updatedState, transaction) =>
        {
            using var staging = new StagingDirectory(storage.Store);
            SafeArchive.ExtractInstaller(
                standardInstaller,
                Path.Combine(staging.Path, RuntimeBuild.Standard.ToString()));
            SafeArchive.ExtractInstaller(
                addonInstaller,
                Path.Combine(staging.Path, RuntimeBuild.FullAddon.ToString()));
            RuntimeService.Deploy(staging.Path, updatedState, transaction, cancellationToken);
            if (updatedState.VulkanManaged)
            {
                vulkan.Enable(updatedState, transaction);
            }

            return Task.CompletedTask;
        }, cancellationToken);
    }

}
