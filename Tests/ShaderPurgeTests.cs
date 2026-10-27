namespace ReShadeCentralized.Tests;

internal static partial class TestSuite
{
    private static async Task ShaderPurgeResolvesUnmanagedPackageConflictsAsync()
    {
        string directory = CreateTestDirectory("shader-purge-reinstall");
        byte[] archive = PackageArchive(directory);
        var store = new StateStore(Path.Combine(directory, "central"));
        using var manager = new Manager(store, new InMemoryLayerRegistry(),
            new Downloads(new StubHttpMessageHandler(_ => PackageResponse(archive))));
        await manager.InitializeAsync(store.DirectoryPath, None);
        ManagerState state = manager.State!;
        ShaderPackage[] packages = [TestPackage("standard", required: true), TestPackage("dependent")];
        string occupied = ShaderService.PackageDirectory(state.Paths.Shaders, "standard");
        Directory.CreateDirectory(occupied);
        File.WriteAllText(Path.Combine(occupied, "Old.fx"), "unmanaged standard effects");
        string custom = Path.Combine(state.Paths.ShaderRoot, "Custom.fx");
        File.WriteAllText(custom, "custom central shader");
        File.WriteAllText(Path.Combine(state.Paths.Textures, "Custom.png"), "custom central texture");
        state.Packages.Add(new InstalledPackage { Id = "old-package", Name = "Old package" });

        GameProfile game = CreateRegistrationGame(state, Path.Combine(directory, "game"));
        game.Name = Path.GetFileName(game.Executable);
        game.Architecture = CpuArchitecture.X64;
        Directory.CreateDirectory(game.Directory);
        string preset = Path.Combine(game.Directory, "ReShadePreset.ini");
        File.WriteAllText(preset, "Techniques=UserPreset\n");
        string config = ConfigurationService.ConfigPath(game);
        File.WriteAllText(config, "[GENERAL]\nPresetPath=" + preset + "\n[Custom]\nKeep=42\n");
        string localConfig = Path.Combine(Path.GetDirectoryName(game.Executable)!, "ReShade.ini");
        File.CreateSymbolicLink(localConfig, config);
        game.Links.Add(new(localConfig, config));
        state.Games.Add(game);
        string localShaders = Path.Combine(Path.GetDirectoryName(game.Executable)!, "reshade-shaders", "Shaders");
        Directory.CreateDirectory(localShaders);
        string local = Path.Combine(localShaders, "Local.fx");
        File.WriteAllText(local, "game-local shader");
        string runtime = Path.Combine(state.Paths.StandardBinaries, "ReShade64.dll");
        string screenshot = Path.Combine(state.Paths.Screenshots, "Screenshot.png");
        File.WriteAllText(runtime, "runtime sentinel");
        File.WriteAllText(screenshot, "screenshot sentinel");

        PackageUpdateReport failed = await manager.UpdatePackagesWithReportAsync(packages, packages, null, None);
        AssertTrue(failed.InstalledCount == 0 && failed.FailedCount == 2);
        AssertTrue(failed.Messages.Any(message =>
            message.Contains("Purge shader folder") && message.Contains(occupied)));
        AssertTrue(File.ReadAllText(custom) == "custom central shader");
        string originalConfig = File.ReadAllText(config);
        await manager.PurgeShaderFolderAsync(null, None);
        AssertTrue(!Directory.Exists(state.Paths.ShaderRoot) && manager.State!.Packages.Count == 0);
        AssertTrue(manager.State!.SelectedPackages.ToHashSet().SetEquals(packages.Select(package => package.Id)));
        AssertTrue(manager.State.Games.Single().Id == game.Id && FileTransaction.Link(localConfig) == config);
        AssertTrue(File.ReadAllText(config) == originalConfig && File.ReadAllText(preset) == "Techniques=UserPreset\n");
        AssertTrue(File.ReadAllText(local) == "game-local shader" && File.ReadAllText(runtime) == "runtime sentinel" &&
            File.ReadAllText(screenshot) == "screenshot sentinel");
        AssertTrue(store.Load()!.Packages.Count == 0 && !Directory.EnumerateFileSystemEntries(store.Recovery).Any());
        await manager.PurgeShaderFolderAsync(null, None);
        PackageUpdateReport reinstalled = await manager.UpdatePackagesWithReportAsync(packages, packages, null, None);
        AssertTrue(reinstalled.InstalledCount == 2 && reinstalled.FailedCount == 0);
        AssertTrue(manager.State.Packages.Count == 2 && Directory.Exists(state.Paths.Shaders));
        AssertTrue(!File.Exists(custom) && File.ReadAllText(preset) == "Techniques=UserPreset\n");
        AssertTrue(IniDocument.Read(config).Get("Custom", "Keep") == "42" && File.Exists(local));
    }

    private static async Task ShaderPurgeRollsBackFailuresAndHonorsCancellationAsync()
    {
        string directory = CreateTestDirectory("shader-purge-rollback");
        byte[] archive = PackageArchive(directory);
        var store = new StateStore(Path.Combine(directory, "central"));
        using var manager = new Manager(store, new InMemoryLayerRegistry(),
            new Downloads(new StubHttpMessageHandler(_ => PackageResponse(archive))));
        await manager.InitializeAsync(store.DirectoryPath, None);
        ShaderPackage package = TestPackage("standard", required: true);
        await manager.UpdatePackagesWithReportAsync([package], [package], null, None);
        string root = manager.State!.Paths.ShaderRoot;
        string loose = Path.Combine(root, "Loose.fx");
        File.WriteAllText(loose, "custom shader before locked package");
        string lockedShader = Path.Combine(manager.State.Paths.Shaders, package.Id, "Test.fx");
        Dictionary<string, string> hashes = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, FileTransaction.Hash);
        string originalState = File.ReadAllText(store.StatePath);
        using (var locked = new FileStream(lockedShader, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await AssertThrowsAsync<IOException>(() => manager.PurgeShaderFolderAsync(null, None));
            AssertTrue(hashes.All(item => FileTransaction.Hash(item.Key) == item.Value));
            AssertTrue(manager.State.Packages.Count == 1 && File.ReadAllText(store.StatePath) == originalState);
            AssertTrue(!Directory.EnumerateFileSystemEntries(store.Recovery).Any());
        }

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(() =>
            manager.PurgeShaderFolderAsync(null, cancellation.Token));
        AssertTrue(hashes.All(item => FileTransaction.Hash(item.Key) == item.Value));
        AssertTrue(File.ReadAllText(store.StatePath) == originalState);
        await manager.PurgeShaderFolderAsync(null, None);
        AssertTrue(!Directory.Exists(root) && manager.State.Packages.Count == 0);
    }

    private static async Task ShaderPurgeRejectsLinkedDirectoriesAndFilesAsync()
    {
        foreach (string variant in new[] { "file", "directory", "root" })
        {
            string directory = CreateTestDirectory("shader-purge-links");
            var store = new StateStore(Path.Combine(directory, "central"));
            using var manager = new Manager(store, new InMemoryLayerRegistry());
            await manager.InitializeAsync(store.DirectoryPath, None);
            CentralPaths paths = manager.State!.Paths;
            string external = Path.Combine(directory, "external");
            Directory.CreateDirectory(external);
            string keep = Path.Combine(external, "Keep.fx");
            File.WriteAllText(keep, "external shader");
            if (variant == "root")
            {
                Directory.Delete(paths.Shaders);
                Directory.Delete(paths.Textures);
                Directory.Delete(paths.ShaderRoot);
                Directory.CreateSymbolicLink(paths.ShaderRoot, external);
            }
            else
            {
                File.WriteAllText(Path.Combine(paths.ShaderRoot, "Central.fx"), "central shader");
                if (variant == "file")
                {
                    File.CreateSymbolicLink(Path.Combine(paths.ShaderRoot, "External.fx"), keep);
                }
                else
                {
                    Directory.CreateSymbolicLink(Path.Combine(paths.ShaderRoot, "External"), external);
                }
            }

            string original = File.ReadAllText(store.StatePath);
            await AssertThrowsAsync<IOException>(() => manager.PurgeShaderFolderAsync(null, None));
            AssertTrue(File.ReadAllText(keep) == "external shader" && File.ReadAllText(store.StatePath) == original);
            if (variant != "root")
            {
                AssertTrue(File.ReadAllText(Path.Combine(paths.ShaderRoot, "Central.fx")) == "central shader");
            }
            AssertTrue(!Directory.EnumerateFileSystemEntries(store.Recovery).Any());
        }
    }
}
