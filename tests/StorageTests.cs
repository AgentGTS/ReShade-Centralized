using System.Text.Json;

namespace ReShadeCentralized.Tests;

internal static partial class TestSuite
{
    private static async Task DefaultStorageOffersFreshSetupGuidanceAsync()
    {
        AssertTrue(Path.IsPathFullyQualified(StateStore.DefaultDirectory));
        string directory = CreateTestDirectory("default-storage-reset");
        string central = Path.Combine(directory, Path.GetFileName(StateStore.DefaultDirectory));
        var store = StateStore.ForApplication(Path.Combine(directory, "app"));
        using (var initial = new Manager(store, new InMemoryLayerRegistry()))
        {
            await initial.InitializeAsync(central, None);
            AssertTrue(Directory.Exists(central) && File.Exists(store.StatePath));
        }

        const string invalid = "{\"SchemaVersion\":-1}";
        File.WriteAllText(store.StatePath, invalid);
        using var reopened = new Manager(store, new InMemoryLayerRegistry());
        try
        {
            reopened.Open();
            throw new Exception("Invalid storage was accepted.");
        }
        catch (InvalidDataException error)
        {
            AssertTrue(error.Message.Contains(StateStore.DefaultDirectory) && error.Message.Contains("delete") &&
                error.Message.Contains("recreated") && error.Message.Contains("unregister affected games"));
        }

        AssertTrue(File.ReadAllText(store.StatePath) == invalid);
        AssertTrue(Path.GetFullPath(central).StartsWith(testRoot + Path.DirectorySeparatorChar));
        Directory.Delete(central, true);
        await reopened.InitializeAsync(central, None);
        AssertTrue(reopened.State!.Paths.Root == central && File.Exists(store.StatePath));
    }

    private static async Task CentralStorageSurvivesApplicationDeletionAsync()
    {
        string directory = CreateTestDirectory("storage-reinstall");
        string application = Path.Combine(directory, "app");
        string central = Path.Combine(directory, "central");
        var registry = new InMemoryLayerRegistry();
        var store = StateStore.ForApplication(application);
        using (var manager = new Manager(store, registry))
        {
            manager.Open();
            AssertTrue(manager.State == null && !Directory.Exists(application));
            await manager.InitializeAsync(central, None);
            AssertTrue(store.DirectoryPath == central && File.Exists(store.StatePath));
            AssertTrue(Directory.GetFiles(application).Single() == store.PointerPath);
            AssertTrue(File.ReadAllText(store.PointerPath!) == central);
            AssertTrue(store.Recovery.StartsWith(central) && store.Staging.StartsWith(central));
        }

        string original = File.ReadAllText(store.StatePath);
        // Leave an operation pending as if the process stopped, then purge only the fixture app folder.
        var interrupted = new FileTransaction(store.Recovery, "Interrupted state update", registry);
        interrupted.WriteText(store.StatePath, "incomplete state");
        string changed = Path.Combine(central, "temporary-file.txt");
        interrupted.WriteText(changed, "uncommitted");
        AssertTrue(Path.GetFullPath(application).StartsWith(testRoot + Path.DirectorySeparatorChar));
        Directory.Delete(application, true);
        string reinstalled = Path.Combine(directory, "fresh-app");
        var freshStore = StateStore.ForApplication(reinstalled);
        using (var manager = new Manager(freshStore, registry))
        {
            manager.Open();
            AssertTrue(manager.State == null && !freshStore.IsSelected);
            await manager.InitializeAsync(central, None);
            AssertTrue(File.ReadAllText(store.StatePath) == original && !File.Exists(changed));
            AssertTrue(!Directory.EnumerateFileSystemEntries(store.Recovery).Any());
            AssertTrue(File.ReadAllText(freshStore.PointerPath!) == central);
        }

        // A subsequent launch follows the pointer without running setup or rewriting central state.
        using var restarted = new Manager(StateStore.ForApplication(reinstalled), registry);
        restarted.Open();
        AssertTrue(restarted.State!.Paths.Root == central && File.ReadAllText(store.StatePath) == original);
    }

    private static async Task StorageSelectionRejectsInvalidFoldersAsync()
    {
        string directory = CreateTestDirectory("storage-selection");
        string application = Path.Combine(directory, "app");
        var store = StateStore.ForApplication(application);
        using var manager = new Manager(store, new InMemoryLayerRegistry());
        foreach (string unsafePath in new[] { "", "relative-folder", application, Path.Combine(application, "data") })
        {
            await AssertThrowsAsync<InvalidDataException>(() => manager.InitializeAsync(unsafePath, None));
            AssertTrue(manager.State == null && !store.IsSelected && !Directory.Exists(application));
        }

        foreach (string contents in new[] { "not JSON", "{\"SchemaVersion\":99}",
            JsonSerializer.Serialize(new ManagerState { Paths = CentralPaths.At(Path.Combine(directory, "elsewhere")) },
                ManagerState.Json) })
        {
            string invalid = CreateTestDirectory("invalid-selected-storage");
            string state = Path.Combine(invalid, "ReShadeCentralized.json");
            File.WriteAllText(state, contents);
            try
            {
                await manager.InitializeAsync(invalid, None);
                throw new Exception("Invalid central storage was accepted.");
            }
            catch (Exception error) when (error is InvalidDataException or JsonException)
            {
                AssertTrue(File.ReadAllText(state) == contents && manager.State == null && !store.IsSelected);
                AssertTrue(!File.Exists(store.PointerPath));
            }
        }

        string central = Path.Combine(directory, "valid");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(() => manager.InitializeAsync(central, cancelled.Token));
        AssertTrue(!Directory.Exists(central) && !File.Exists(store.PointerPath));
        await manager.InitializeAsync(central, None);
        AssertTrue(manager.State!.Paths.Root == central);
    }

    private static async Task StoragePointerFailuresRemainRecoverableAsync()
    {
        string directory = CreateTestDirectory("storage-pointer");
        string application = Path.Combine(directory, "app");
        Directory.CreateDirectory(application);
        var store = StateStore.ForApplication(application);
        string missing = Path.Combine(directory, "missing-storage");
        File.WriteAllText(store.PointerPath!, missing);
        using (var manager = new Manager(store, new InMemoryLayerRegistry()))
        {
            AssertThrows<IOException>(manager.Open);
            AssertTrue(!Directory.Exists(missing) && File.ReadAllText(store.PointerPath!) == missing);
            await manager.InitializeAsync(Path.Combine(directory, "central"), None);
            AssertTrue(File.ReadAllText(store.PointerPath!) == manager.State!.Paths.Root);
        }

        string blockedApp = Path.Combine(directory, "blocked-app");
        Directory.CreateDirectory(Path.Combine(blockedApp, "CentralStorage.txt"));
        var warnings = new List<string>();
        using var blocked = new Manager(StateStore.ForApplication(blockedApp), new InMemoryLayerRegistry());
        blocked.Warning += warnings.Add;
        string valid = Path.Combine(directory, "other-central");
        await blocked.InitializeAsync(valid, None);
        AssertTrue(blocked.State!.Paths.Root == valid && File.Exists(blocked.Store.StatePath));
        AssertTrue(warnings.Single().Contains("next launch"));
        AssertTrue(!Directory.EnumerateFileSystemEntries(blocked.Store.Recovery).Any());
    }

    private static async Task RegisteredGamesSurviveApplicationDeletionAsync()
    {
        string directory = CreateTestDirectory("registered-storage-reinstall");
        string application = Path.Combine(directory, "app");
        string central = Path.Combine(directory, "central");
        var registry = new InMemoryLayerRegistry();
        string standard = Path.Combine(repositoryRoot, ".validation", "ReShade_Setup_6.8.0.exe");
        string addon = Path.Combine(repositoryRoot, ".validation", "ReShade_Setup_6.8.0_Addon.exe");
        string dxgiId;
        string vulkanId;
        using (var manager = new Manager(StateStore.ForApplication(application), registry))
        {
            await manager.InitializeAsync(central, None);
            await manager.ImportRuntimeAsync(standard, addon, None);
            GameProfile directX = CreateRegistrationGame(manager.State!, Path.Combine(directory, "directx-game"));
            GameProfile vulkan = CreateRegistrationGame(manager.State!, Path.Combine(directory, "vulkan-game"));
            vulkan.Api = RenderApi.Vulkan;
            await manager.InstallAsync(directX, None);
            await manager.InstallAsync(vulkan, None);
            dxgiId = directX.Id;
            vulkanId = vulkan.Id;
            AssertTrue(manager.State!.VulkanLayerDirectory == Path.Combine(central, "Vulkan"));
            File.WriteAllText(Path.Combine(manager.State.Games.First().Directory, "ReShadePreset.ini"), "user preset");
            ShaderPackage package = TestPackage("reinstall-package");
            byte[] archive = PackageArchive(directory);
            using var downloads = new Downloads(new StubHttpMessageHandler(_ => PackageResponse(archive)));
            using var packages = new Manager(new StateStore(central), registry, downloads);
            packages.Open();
            await packages.UpdatePackagesWithReportAsync([package], [package], null, None);
        }

        Dictionary<string, string> hashes = Directory.GetFiles(central, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, FileTransaction.Hash);
        Dictionary<LayerRegistration, int> registrations = registry.Values.ToDictionary();
        registry.WriteHistory.Clear();
        AssertTrue(Path.GetFullPath(application).StartsWith(testRoot + Path.DirectorySeparatorChar));
        Directory.Delete(application, true);
        AssertTrue(registrations.Keys.All(key => File.Exists(key.Manifest)));
        var freshStore = StateStore.ForApplication(Path.Combine(directory, "redownloaded-app"));
        using var fresh = new Manager(freshStore, registry);
        fresh.Open();
        AssertTrue(fresh.State == null);
        await fresh.InitializeAsync(central, None);
        AssertTrue(fresh.State!.Games.Select(game => game.Id).ToHashSet().SetEquals([dxgiId, vulkanId]));
        AssertTrue(fresh.State.Games.All(game => DeploymentService.GetInstallationWarning(game) == null));
        AssertTrue(fresh.State.Packages.Single().Id == "reinstall-package");
        AssertTrue(fresh.State.SelectedPackages.Contains("reinstall-package"));
        AssertTrue(fresh.State.RuntimeVersions.Count == 2 && fresh.State.OwnedVulkanApps.Count == 1);
        AssertTrue(hashes.All(item => FileTransaction.Hash(item.Key) == item.Value));
        AssertTrue(registry.WriteHistory.Count == 0 &&
            registrations.All(item => registry.Read(item.Key) == item.Value));
        await fresh.RepairAsync(null, None);
        AssertTrue(fresh.State.Games.All(game => game.Links.All(DeploymentService.IsOwnedLink)));
        string preset = Path.Combine(fresh.State.Games.First().Directory, "ReShadePreset.ini");
        AssertTrue(File.ReadAllText(preset) == "user preset");
        await fresh.UnregisterAsync(dxgiId, None);
        await fresh.UnregisterAsync(vulkanId, None);
        AssertTrue(fresh.State.Games.Count == 0 && registry.WriteHistory.Count == 0);
        AssertTrue(registrations.All(item => registry.Read(item.Key) == item.Value));
    }
}
