using System.Net;

namespace ReShadeCentralized.Tests;

internal static partial class TestSuite
{
    private static async Task CatalogCachesReplaceBundledSnapshotsAsync()
    {
        AssertTrue(!typeof(ShaderService).Assembly.GetManifestResourceNames().Any(name =>
            name.EndsWith(".Compatibility.ini") || name.EndsWith(".EffectPackages.ini")));
        string directory = CreateTestDirectory("downloaded-catalogs");
        var store = new StateStore(directory);
        const string shaderText = "[standard]\nRepositoryUrl=https://github.com/test/standard\n" +
            "DownloadUrl=https://example.test/standard.zip\nRequired=1\n";
        using var downloads = new Downloads(new StubHttpMessageHandler(request => new(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("Compatibility.ini") ?
                "[Fixture.exe]\nRenderApi=DXGI\n" : shaderText)
        }));
        var service = new ShaderService(downloads, store);
        List<ShaderPackage> packages = await service.CatalogAsync(null, None);
        AssertTrue(packages.Any(package => package.Required));
        string shaderCache = Path.Combine(directory, ShaderService.CacheName);
        string compatibilityCache = Path.Combine(directory, CompatibilityCatalog.CacheName);
        AssertTrue(File.ReadAllText(shaderCache) == shaderText);
        AssertTrue(CompatibilityCatalog.Default.Find("Fixture.exe")?.Api == RenderApi.DXGI);
        string original = File.ReadAllText(compatibilityCache);
        using var invalid = new Downloads(new StubHttpMessageHandler(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent("invalid catalog")
        }));
        List<ShaderPackage> cached = await new ShaderService(invalid, store).CatalogAsync(null, None);
        AssertTrue(cached.Select(package => package.Id).SequenceEqual(packages.Select(package => package.Id)));
        AssertTrue(File.ReadAllText(shaderCache) == shaderText && File.ReadAllText(compatibilityCache) == original);
        CompatibilityCatalog.LoadCache(compatibilityCache);
        AssertTrue(CompatibilityCatalog.Default.Find("Fixture.exe")?.Api == RenderApi.DXGI);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(() =>
            service.CatalogAsync(null, canceled.Token));
        AssertTrue(File.ReadAllText(compatibilityCache) == original);
        AssertTrue(!Directory.EnumerateFiles(directory, "*.tmp").Any());

        string linkRoot = CreateTestDirectory("linked-catalog-cache");
        string linked = Path.Combine(linkRoot, CompatibilityCatalog.CacheName);
        File.CreateSymbolicLink(linked, compatibilityCache);
        await CompatibilityCatalog.RefreshAsync(downloads, linked, null, None);
        AssertTrue(CompatibilityCatalog.Default.Count == 0 && File.ReadAllText(compatibilityCache) == original);
    }

    private static Task ShaderNoticesSurviveInstallationAndUpdatesAsync()
    {
        string directory = CreateTestDirectory("shader-notices");
        string source = Path.Combine(directory, "archive", "repo");
        Directory.CreateDirectory(Path.Combine(source, "Shaders"));
        Directory.CreateDirectory(Path.Combine(source, "licenses"));
        File.WriteAllText(Path.Combine(source, "Shaders", "test.fx"), "// Copyright: fixture author\n");
        File.WriteAllText(Path.Combine(source, "LICENSE"), "Fixture license text\n");
        File.WriteAllText(Path.Combine(source, "NOTICE.md"), "Fixture attribution\n");
        File.WriteAllText(Path.Combine(source, "README.md"), "Additional usage terms\n");
        File.WriteAllText(Path.Combine(source, "licenses", "ThirdParty.txt"), "Dependency notice\n");
        File.WriteAllText(Path.Combine(source, "LICENSE.exe"), "Not a document");
        ManagerState state = CreateState(Path.Combine(directory, "central"));
        ShaderPackage package = TestPackage("notice-fixture");
        InstalledPackage installed;
        using (var transaction = new FileTransaction(Path.Combine(directory, "recovery"), "retain notices"))
        {
            installed = ShaderService.InstallExtracted(package,
                Path.GetDirectoryName(source)!, state, transaction, None);
            transaction.Commit();
        }
        string notices = Path.Combine(state.Paths.Shaders, package.Id, "_notices", "repo");
        AssertTrue(File.ReadAllText(Path.Combine(notices, "LICENSE")) == "Fixture license text\n");
        AssertTrue(File.Exists(Path.Combine(notices, "licenses", "ThirdParty.txt")) &&
            File.Exists(Path.Combine(notices, "README.md")) && !File.Exists(Path.Combine(notices, "LICENSE.exe")));
        AssertTrue(installed.Files.Count(key => key.Key.StartsWith("shaders/_notices/")) == 4);
        ShaderService.CheckUnmodified(installed, state.Paths);
        File.Delete(Path.Combine(source, "NOTICE.md"));
        File.WriteAllText(Path.Combine(source, "LICENSE"), "Updated fixture license\n");
        using (var transaction = new FileTransaction(Path.Combine(directory, "recovery"), "update notices"))
        {
            installed = ShaderService.InstallExtracted(package,
                Path.GetDirectoryName(source)!, state, transaction, None);
            transaction.Commit();
        }
        AssertTrue(!File.Exists(Path.Combine(notices, "NOTICE.md")) &&
            File.ReadAllText(Path.Combine(notices, "LICENSE")) == "Updated fixture license\n");
        File.WriteAllText(Path.Combine(notices, "LICENSE"), "Changed local notice");
        AssertThrows<IOException>(() => ShaderService.CheckUnmodified(installed, state.Paths));
        return Task.CompletedTask;
    }
}
