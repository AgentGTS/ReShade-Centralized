namespace ReShadeCentralized.Tests;

internal static partial class TestSuite
{
    private static async Task OnlineRuntimeDownloadValidatesOfficialPayloadsAsync(Downloads downloads)
    {
        Dictionary<RuntimeBuild, string> versions = await new RuntimeService(downloads).StageAsync(
            CreateTestDirectory("online-runtimes"),
            null,
            None);
        AssertTrue(versions.Count == 2 && versions.Values.Distinct().Count() == 1);
    }

    private static async Task ShaderArchivePreservesTheCurrentPackageLayoutAsync(
        Downloads downloads,
        List<ShaderPackage> catalog,
        string name)
    {
        ShaderPackage package = catalog.Single(packageItem => packageItem.Repository.EndsWith(
                name,
                StringComparison.OrdinalIgnoreCase));
        string directory = CreateTestDirectory("online-shader");
        var service = new ShaderService(downloads, new StateStore(directory));
        string extracted = await service.StageAsync(package, Path.Combine(directory, "stage"), null, None);
        ManagerState state = CreateState(Path.Combine(directory, "central"));
        using var transaction = new FileTransaction(Path.Combine(directory, "backups"), "online shader");
        InstalledPackage installed = ShaderService.InstallExtracted(package, extracted, state, transaction, None);
        transaction.Commit();
        AssertTrue(installed.Files.Count > 0);
        ShaderService.CheckUnmodified(installed, state.Paths);
    }
}
