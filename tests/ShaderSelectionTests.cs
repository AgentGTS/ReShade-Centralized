using System.Text.Json;

namespace ReShadeCentralized.Tests;

internal static partial class TestSuite
{
    private sealed class SelectionProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private static async Task ShaderSelectionInstallsUpdatesAndRemovesAsync()
    {
        string directory = CreateTestDirectory("apply-shader-selection");
        byte[] archive = PackageArchive(directory);
        var store = new StateStore(Path.Combine(directory, "central"));
        using var manager = new Manager(store, new InMemoryLayerRegistry(),
            new Downloads(new StubHttpMessageHandler(_ => PackageResponse(archive))));
        await manager.InitializeAsync(store.DirectoryPath, None);
        ShaderPackage standard = TestPackage("standard", required: true);
        ShaderPackage keep = TestPackage("keep");
        ShaderPackage remove = TestPackage("remove");
        ShaderPackage add = TestPackage("add");
        ShaderPackage[] catalog = [standard, keep, remove, add];
        GameProfile game = CreateRegistrationGame(manager.State!, Path.Combine(directory, "game"));
        game.Name = Path.GetFileName(game.Executable);
        game.Architecture = CpuArchitecture.X64;
        manager.State!.Games.Add(game);
        await manager.UpdatePackagesWithReportAsync([standard, keep, remove], catalog, null, None);
        string removedShaders = ShaderService.PackageDirectory(manager.State.Paths.Shaders, remove.Id);
        string removedTextures = ShaderService.PackageDirectory(manager.State.Paths.Textures, remove.Id);
        Directory.CreateDirectory(removedTextures);
        File.WriteAllText(Path.Combine(removedShaders, "Test.fx"), "edited package shader");
        File.WriteAllText(Path.Combine(removedShaders, "Custom.fx"), "extra custom shader");
        File.WriteAllText(Path.Combine(removedTextures, "Custom.png"), "extra custom texture");
        string external = Path.Combine(directory, "custom-effects");
        Directory.CreateDirectory(external);
        string configPath = ConfigurationService.ConfigPath(game);
        IniDocument config = IniDocument.Read(configPath);
        config.Set("GENERAL", "EffectSearchPaths", config.Get("GENERAL", "EffectSearchPaths") + "," + external);
        config.Set("GENERAL", "TextureSearchPaths", IniDocument.Join([Path.Combine(removedTextures, "**"), external]));
        File.WriteAllText(configPath, config.ToString());
        string preset = Path.Combine(game.Directory, "ReShadePreset.ini");
        File.WriteAllText(preset, "User preset remains selected");

        ShaderSelectionPlan plan = manager.CreateShaderSelectionPlan([keep, add], catalog);
        AssertTrue(plan.Selected.Any(package => package.Required) && plan.Removals.Single().Id == remove.Id);
        AssertThrows<InvalidOperationException>(() => manager.ApplyShaderSelectionAsync(plan, catalog, null, None));
        await AssertThrowsAsync<IOException>(() =>
            manager.ApplyShaderSelectionAsync(plan with { Removals = [] }, catalog, null, None));
        AssertTrue(File.ReadAllText(Path.Combine(removedShaders, "Custom.fx")) == "extra custom shader");
        PackageUpdateReport report = await manager.ApplyShaderSelectionAsync(plan, catalog, null, None, true);
        AssertTrue(report.InstalledCount == 3 && report.RemovedCount == 1 && report.FailedCount == 0);
        AssertTrue(!Directory.Exists(removedShaders) && !Directory.Exists(removedTextures));
        AssertTrue(manager.State.Packages.Select(package => package.Id)
            .ToHashSet().SetEquals(["standard", "keep", "add"]));
        AssertTrue(manager.State.SelectedPackages.ToHashSet().SetEquals(["standard", "keep", "add"]));
        AssertTrue(File.ReadAllText(preset) == "User preset remains selected");
        config = IniDocument.Read(configPath);
        AssertTrue(!config.Get("GENERAL", "EffectSearchPaths")!.Contains(removedShaders));
        AssertTrue(!config.Get("GENERAL", "TextureSearchPaths")!.Contains(removedTextures));
        AssertTrue(config.Get("GENERAL", "EffectSearchPaths")!.Contains(external));
        AssertTrue(config.Get("GENERAL", "TextureSearchPaths")!.Contains(external));
        AssertTrue(!Directory.EnumerateFileSystemEntries(store.Recovery).Any());

        ShaderSelectionPlan stale = manager.CreateShaderSelectionPlan([standard], catalog);
        await manager.UpdatePackagesWithReportAsync([TestPackage("another")], [standard], null, None);
        await AssertThrowsAsync<IOException>(() => manager.ApplyShaderSelectionAsync(stale, catalog, null, None, true));
        AssertTrue(manager.State.Packages.Any(package => package.Id == keep.Id));
    }

    private static async Task ShaderRemovalFailureRetainsDependenciesAndIntentAsync()
    {
        string directory = CreateTestDirectory("remove-dependency-failure");
        byte[] archive = PackageArchive(directory);
        var store = new StateStore(Path.Combine(directory, "central"));
        using var manager = new Manager(store, new InMemoryLayerRegistry(),
            new Downloads(new StubHttpMessageHandler(_ => PackageResponse(archive))));
        await manager.InitializeAsync(store.DirectoryPath, None);
        ShaderPackage standard = TestPackage("standard", required: true);
        ShaderPackage framework = TestPackage("framework", "https://github.com/Zenteon/ZenteonFX");
        ShaderPackage dependent = TestPackage("dependent", "https://github.com/yplebedev/BFBFX");
        ShaderPackage[] catalog = [standard, framework, dependent];
        await manager.UpdatePackagesWithReportAsync([dependent], catalog, null, None);
        string shader = Path.Combine(manager.State!.Paths.Shaders, dependent.Id, "Test.fx");
        ShaderSelectionPlan plan = manager.CreateShaderSelectionPlan([], catalog);
        AssertTrue(plan.Selected.Single().Id == standard.Id && plan.Removals.Count == 2);
        using (var locked = new FileStream(shader, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            PackageUpdateReport failed = await manager.ApplyShaderSelectionAsync(plan, catalog, null, None, true);
            AssertTrue(failed.RemovedCount == 0 && failed.FailedCount == 2);
            AssertTrue(failed.Messages.Any(message => message.Contains("still required by")));
            AssertTrue(manager.State.Packages.Count == 3 && File.Exists(shader));
        }

        using (var reopened = new Manager(new StateStore(store.DirectoryPath), new InMemoryLayerRegistry()))
        {
            reopened.Open();
            AssertTrue(reopened.State!.Packages.Count == 3 &&
                reopened.State.SelectedPackages.SequenceEqual([standard.Id]));
        }
        plan = manager.CreateShaderSelectionPlan([], catalog);
        PackageUpdateReport removed = await manager.ApplyShaderSelectionAsync(plan, catalog, null, None, true);
        AssertTrue(removed.RemovedCount == 2 && removed.FailedCount == 0 && manager.State.Packages.Count == 1);
    }

    private static async Task ShaderRemovalCancellationPreservesCompletedChangesAsync()
    {
        string directory = CreateTestDirectory("remove-cancellation");
        byte[] archive = PackageArchive(directory);
        var store = new StateStore(Path.Combine(directory, "central"));
        using var manager = new Manager(store, new InMemoryLayerRegistry(),
            new Downloads(new StubHttpMessageHandler(_ => PackageResponse(archive))));
        await manager.InitializeAsync(store.DirectoryPath, None);
        ShaderPackage[] catalog =
            [TestPackage("standard", required: true), TestPackage("a-remove"), TestPackage("z-remove")];
        await manager.UpdatePackagesWithReportAsync(catalog, catalog, null, None);
        ShaderSelectionPlan plan = manager.CreateShaderSelectionPlan([], catalog);
        using var cancellation = new CancellationTokenSource();
        var progress = new SelectionProgress(message =>
        {
            if (message.StartsWith("Removed:"))
            {
                cancellation.Cancel();
            }
        });
        await AssertThrowsAsync<OperationCanceledException>(() =>
            manager.ApplyShaderSelectionAsync(plan, catalog, progress, cancellation.Token, true));
        AssertTrue(manager.State!.Packages.Count == 2 &&
            !manager.State.Packages.Any(package => package.Id == "a-remove"));
        AssertTrue(manager.State.SelectedPackages.SequenceEqual(["standard"]));
        AssertTrue(store.Load()!.Packages.Count == 2 && !Directory.EnumerateFileSystemEntries(store.Recovery).Any());
    }

    private static Task ShaderSelectionMigrationAndAddonPathsAsync()
    {
        string directory = CreateTestDirectory("selection-migration-addon-paths");
        var store = new StateStore(directory);
        ManagerState state = CreateState(directory);
        state.SchemaVersion = 3;
        state.Packages.Add(new InstalledPackage { Id = "installed", Name = "Installed" });
        state.SelectedPackages.Add("requested");
        File.WriteAllText(store.StatePath, JsonSerializer.Serialize(state, ManagerState.Json));
        using var manager = new Manager(store, new InMemoryLayerRegistry());
        manager.Open();
        AssertTrue(manager.State!.SchemaVersion == 4 &&
            manager.State.SelectedPackages.ToHashSet().SetEquals(["requested", "installed"]));
        GameProfile game = CreateRegistrationGame(state, Path.Combine(directory, "game"));
        Directory.CreateDirectory(game.Directory);
        string basePath = Path.Combine(directory, "original-base");
        string custom = Path.Combine(basePath, "custom,addons");
        string initial = "[INSTALL]\nBasePath=" + basePath +
            "\n[ADDON]\nAddonPath=custom,,addons,ignored-second-path\nDisabledAddons=Keep@Keep.addon64\n";
        using var transaction = new FileTransaction(store.Recovery, "Test add-on configuration");
        ConfigurationService.Update(state, game, transaction, initial);
        transaction.Commit();
        IniDocument config = IniDocument.Read(ConfigurationService.ConfigPath(game));
        AssertTrue(IniDocument.Split(config.Get("ADDON", "AddonPath")).SequenceEqual([custom]));
        AssertTrue(config.Get("ADDON", "DisabledAddons") == "Keep@Keep.addon64");
        AssertTrue(IniDocument.Split(config.Get("INSTALL", "BasePath")).Single() == game.Directory);
        return Task.CompletedTask;
    }

    private static async Task ShaderRemovalRejectsLinkedFoldersAsync()
    {
        string directory = CreateTestDirectory("selection-remove-linked-folder");
        byte[] archive = PackageArchive(directory);
        var store = new StateStore(Path.Combine(directory, "central"));
        using var manager = new Manager(store, new InMemoryLayerRegistry(),
            new Downloads(new StubHttpMessageHandler(_ => PackageResponse(archive))));
        await manager.InitializeAsync(store.DirectoryPath, None);
        ShaderPackage standard = TestPackage("standard", required: true);
        ShaderPackage linked = TestPackage("linked");
        string external = Path.Combine(directory, "external");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "Keep.fx"), "Keep external data");
        string packageFolder = ShaderService.PackageDirectory(manager.State!.Paths.Shaders, linked.Id);
        Directory.CreateSymbolicLink(packageFolder, external);
        manager.State.Packages.Add(new InstalledPackage { Id = linked.Id, Name = linked.Name });
        ShaderSelectionPlan plan = manager.CreateShaderSelectionPlan([], [standard, linked]);
        PackageUpdateReport report = await manager.ApplyShaderSelectionAsync(
            plan, [standard, linked], null, None, true);
        AssertTrue(report.RemovedCount == 0 && report.FailedCount == 1);
        AssertTrue(manager.State.Packages.Any(package => package.Id == linked.Id));
        AssertTrue(File.ReadAllText(Path.Combine(external, "Keep.fx")) == "Keep external data");
        AssertThrows<InvalidDataException>(() =>
            ShaderService.PackageDirectory(manager.State.Paths.Shaders, "../external"));
        AssertThrows<InvalidDataException>(() => manager.CreateShaderSelectionPlan([], []));
    }
}
