using System.Net;

namespace ReShadeCentralized.Tests;

internal static partial class TestSuite
{
    private static ShaderPackage TestPackage(string id, string? repository = null, bool required = false) => new()
    {
        Id = id,
        Name = id,
        Repository = repository ?? $"https://github.com/test/{id}",
        DownloadUrl = $"https://example.test/{id}.zip",
        Required = required
    };

    private static byte[] PackageArchive(string directory)
    {
        string path = Path.Combine(directory, "package.zip");
        CreateArchive(path, ("repo/Shaders/Test.fx", "// original shader"));
        return File.ReadAllBytes(path);
    }

    private static HttpResponseMessage PackageResponse(byte[] archive) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(archive)
    };

    private static async Task<T> AssertThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T error)
        {
            return error;
        }

        throw new Exception("Expected " + typeof(T).Name);
    }

    private static async Task PackageFailuresDoNotBlockHealthyDownloadsAsync()
    {
        string directory = CreateTestDirectory("partial-package-downloads");
        byte[] archive = PackageArchive(directory);
        ShaderPackage[] packages =
        [
            TestPackage("standard", required: true), TestPackage("a-missing"), TestPackage("b-timeout"),
            TestPackage("c-corrupt"), TestPackage("d-healthy"), TestPackage("e-healthy")
        ];
        var requests = new List<string>();
        var handler = new StubHttpMessageHandler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            requests.Add(path);
            return path switch
            {
                "/a-missing.zip" => new(HttpStatusCode.NotFound),
                "/b-timeout.zip" => throw new TaskCanceledException("Simulated download timeout"),
                "/c-corrupt.zip" => PackageResponse([1, 2, 3]),
                _ => PackageResponse(archive)
            };
        });
        var store = new StateStore(Path.Combine(directory, "manager"));
        using var manager = new Manager(store, new InMemoryLayerRegistry(), new Downloads(handler));
        await manager.InitializeAsync(Path.Combine(directory, "central"), None);
        PackageUpdateReport report = await manager.UpdatePackagesWithReportAsync(packages, packages, null, None);

        AssertTrue(report.InstalledCount == 3 && report.FailedCount == 3, report.Summary);
        AssertTrue(requests.Count == 6 && requests.Contains("/e-healthy.zip"));
        AssertTrue(manager.State!.Packages.Select(package => package.Id).Order()
            .SequenceEqual(new[] { "d-healthy", "e-healthy", "standard" }));
        AssertTrue(store.Load()!.Packages.Count == 3 && store.Load()!.SelectedPackages.Count == 6);
        AssertTrue(report.Messages.Any(message => message.Contains("timed out")));
        AssertTrue(report.Messages.Any(message => message.Contains("Refresh catalog")));
        AssertTrue(report.Messages.Where(message => message.StartsWith("Failed "))
            .All(message => message.Contains("apply the selection again")));
        AssertTrue(!Directory.EnumerateDirectories(store.Staging).Any());
    }

    private static async Task FailedPackageUpdatesPreserveExistingFilesAndLocalEditsAsync()
    {
        string directory = CreateTestDirectory("retained-packages");
        byte[] archive = PackageArchive(directory);
        ShaderPackage existing = TestPackage("a-existing");
        ShaderPackage healthy = TestPackage("z-healthy");
        bool failExisting = false;
        var handler = new StubHttpMessageHandler(request =>
            failExisting && request.RequestUri!.AbsolutePath == "/a-existing.zip" ?
                new(HttpStatusCode.ServiceUnavailable) : PackageResponse(archive));
        var store = new StateStore(Path.Combine(directory, "manager"));
        using var manager = new Manager(store, new InMemoryLayerRegistry(), new Downloads(handler));
        await manager.InitializeAsync(Path.Combine(directory, "central"), None);
        await manager.UpdatePackagesAsync([existing], [existing], null, None);
        string shader = Path.Combine(manager.State!.Paths.Shaders, existing.Id, "Test.fx");
        string originalHash = FileTransaction.Hash(shader);
        DateTimeOffset originalUpdated = manager.State.Packages.Single().Updated;

        failExisting = true;
        PackageUpdateReport report = await manager.UpdatePackagesWithReportAsync(
            [existing, healthy], [existing, healthy], null, None);
        AssertTrue(report.InstalledCount == 1 && report.FailedCount == 1);
        AssertTrue(FileTransaction.Hash(shader) == originalHash);
        AssertTrue(store.Load()!.Packages.Single(package => package.Id == existing.Id).Updated == originalUpdated);

        File.WriteAllText(shader, "// local edits");
        report = await manager.UpdatePackagesWithReportAsync([existing, healthy], [existing, healthy], null, None);
        AssertTrue(report.InstalledCount == 1 && report.FailedCount == 1);
        AssertTrue(File.ReadAllText(shader) == "// local edits");
        AssertTrue(report.Messages.Any(message => message.Contains("Purge shader folder")));
    }

    private static async Task FailedDependenciesSkipOnlyAffectedPackagesAsync()
    {
        string directory = CreateTestDirectory("package-dependencies");
        byte[] archive = PackageArchive(directory);
        ShaderPackage standard = TestPackage("standard", required: true);
        ShaderPackage framework = TestPackage("z-framework", "https://github.com/Zenteon/ZenteonFX");
        ShaderPackage dependent = TestPackage("a-bfb", "https://github.com/yplebedev/BFBFX");
        ShaderPackage unrelated = TestPackage("b-unrelated");
        ShaderPackage[] catalog = [standard, framework, dependent, unrelated];
        bool failFramework = true;
        var requests = new List<string>();
        var handler = new StubHttpMessageHandler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            requests.Add(path);
            return failFramework && path == "/z-framework.zip" ?
                new(HttpStatusCode.ServiceUnavailable) : PackageResponse(archive);
        });
        using var manager = new Manager(new StateStore(Path.Combine(directory, "manager")),
            new InMemoryLayerRegistry(), new Downloads(handler));
        await manager.InitializeAsync(Path.Combine(directory, "central"), None);
        PackageUpdateReport report = await manager.UpdatePackagesWithReportAsync(
            [dependent, unrelated], catalog, null, None);
        AssertTrue(report.InstalledCount == 2 && report.FailedCount == 2);
        AssertTrue(!requests.Contains("/a-bfb.zip") && requests.Contains("/b-unrelated.zip"));
        AssertTrue(report.Messages.Any(message => message.Contains("Skipped a-bfb") &&
            message.Contains("z-framework")));

        failFramework = false;
        report = await manager.UpdatePackagesWithReportAsync([dependent, unrelated], catalog, null, None);
        AssertTrue(report.InstalledCount == 4 && report.FailedCount == 0);
        failFramework = true;
        report = await manager.UpdatePackagesWithReportAsync([dependent, unrelated], catalog, null, None);
        AssertTrue(report.InstalledCount == 3 && report.FailedCount == 1,
            "A retained, complete dependency should remain usable after a failed update.");

        ShaderPackage[] incompleteCatalog = [standard, dependent, unrelated];
        report = await manager.UpdatePackagesWithReportAsync(
            [dependent, unrelated], incompleteCatalog, null, None);
        AssertTrue(report.InstalledCount == 2 && report.FailedCount == 1);
        AssertTrue(manager.State!.SelectedPackages.Contains(dependent.Id));
    }

    private static async Task PackageCancellationKeepsCompletedInstallsAsync()
    {
        string directory = CreateTestDirectory("package-cancellation");
        byte[] archive = PackageArchive(directory);
        ShaderPackage[] packages = [TestPackage("a-completed"), TestPackage("z-canceled")];
        using var cancellation = new CancellationTokenSource();
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/z-canceled.zip")
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }

            return PackageResponse(archive);
        });
        var store = new StateStore(Path.Combine(directory, "manager"));
        using var manager = new Manager(store, new InMemoryLayerRegistry(), new Downloads(handler));
        await manager.InitializeAsync(Path.Combine(directory, "central"), None);
        await AssertThrowsAsync<OperationCanceledException>(() => manager.UpdatePackagesWithReportAsync(
            packages, packages, null, cancellation.Token));
        AssertTrue(manager.State!.Packages.Single().Id == "a-completed");
        AssertTrue(store.Load()!.Packages.Single().Id == "a-completed");
        AssertTrue(!Directory.EnumerateDirectories(store.Staging).Any());
    }

    private static async Task PackageCleanupAndStagingFailuresAreIsolatedAsync()
    {
        string directory = CreateTestDirectory("package-cleanup");
        byte[] archive = PackageArchive(directory);
        ShaderPackage[] packages = [TestPackage("a-locked-cleanup"), TestPackage("z-healthy")];
        var store = new StateStore(Path.Combine(directory, "manager"));
        FileStream? cleanupLock = null;
        string? lockedDirectory = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/a-locked-cleanup.zip")
            {
                lockedDirectory = Directory.GetDirectories(store.Staging).Single();
                cleanupLock = new FileStream(Path.Combine(lockedDirectory, "cleanup.lock"),
                    FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }

            return PackageResponse(archive);
        });
        using var manager = new Manager(store, new InMemoryLayerRegistry(), new Downloads(handler));
        await manager.InitializeAsync(Path.Combine(directory, "central"), None);
        try
        {
            PackageUpdateReport report = await manager.UpdatePackagesWithReportAsync(packages, packages, null, None);
            AssertTrue(report.InstalledCount == 2 && report.FailedCount == 0);
            AssertTrue(report.Messages.Any(message => message.Contains("Could not remove temporary files")));
            AssertTrue(manager.State!.Packages.Count == 2);
        }
        finally
        {
            cleanupLock?.Dispose();
            if (lockedDirectory != null)
            {
                Directory.Delete(lockedDirectory, true);
            }
        }

        Directory.Delete(store.Staging);
        File.WriteAllText(store.Staging, "blocks staging creation");
        PackageUpdateReport blocked = await manager.UpdatePackagesWithReportAsync(packages, packages, null, None);
        AssertTrue(blocked.InstalledCount == 0 && blocked.FailedCount == 2);
        AssertTrue(blocked.Messages.Where(message => message.StartsWith("Failed "))
            .All(message => message.Contains("writable folder")));
        AssertTrue(store.Load()!.Packages.Count == 2);
    }

    private static async Task OutOfOrderActionsExplainRecoveryAsync()
    {
        string directory = CreateTestDirectory("action-order");
        using var manager = new Manager(new StateStore(Path.Combine(directory, "manager")),
            new InMemoryLayerRegistry());
        InvalidOperationException setup = await AssertThrowsAsync<InvalidOperationException>(
            () => manager.UpdatePackagesAsync([], [], null, None));
        AssertTrue(setup.Message.Contains("Setup"));
        await manager.InitializeAsync(Path.Combine(directory, "central"), None);
        InvalidOperationException vulkan = await AssertThrowsAsync<InvalidOperationException>(
            () => manager.SetVulkanBuildAsync(RuntimeBuild.Standard, None));
        AssertTrue(vulkan.Message.Contains("Runtime & storage > Update ReShade"));
        AssertTrue(!manager.State!.VulkanManaged && manager.State.VulkanLayerFiles.Count == 0);

        FileNotFoundException executable = await AssertThrowsAsync<FileNotFoundException>(
            () => manager.InstallAsync(new GameProfile { Name = "Missing game", Executable = "" }, None));
        AssertTrue(executable.Message.Contains("Games > Install game or Repair / edit"));
        string gameExecutable = Path.Combine(directory, "game.exe");
        File.WriteAllBytes(gameExecutable, CreateExecutableWithImports(true, ["dxgi.dll"]));
        IOException runtime = await AssertThrowsAsync<IOException>(() => manager.InstallAsync(
            new GameProfile { Name = "No runtime", Executable = gameExecutable }, None));
        AssertTrue(runtime.Message.Contains("Runtime & storage > Update ReShade"));
        AssertTrue(manager.State.Games.Count == 0 && !File.Exists(Path.Combine(directory, "ReShade.ini")));

        AssertTrue(UserErrors.Describe(new UnauthorizedAccessException("denied")).Contains("administrator"));
        AssertTrue(UserErrors.Describe(new IOException("locked", unchecked((int)0x80070020)))
            .Contains("Close affected games"));
        AssertTrue(UserErrors.Describe(new HttpRequestException("missing", null, HttpStatusCode.NotFound))
            .Contains("Refresh catalog"));
    }
}
