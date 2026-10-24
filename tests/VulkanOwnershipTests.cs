namespace ReShadeCentralized.Tests;

internal static partial class TestSuite
{
    private static Task ExistingVulkanLayersRetainRegistrationsAndForeignEntriesAsync()
    {
        string directory = CreateTestDirectory("existing-shared-vulkan");
        string shared = Path.Combine(directory, "native-reshade");
        Directory.CreateDirectory(shared);
        foreach (string file in Directory.GetFiles(Path.Combine(runtimePayload, "Standard")))
        {
            File.Copy(file, Path.Combine(shared, Path.GetFileName(file)));
        }

        var registry = new InMemoryLayerRegistry();
        var x86 = new LayerRegistration("HKCU", "32", Path.Combine(shared, "ReShade32.json"));
        var x64 = new LayerRegistration("HKLM", "64", Path.Combine(shared, "ReShade64.json"));
        registry.Values[x86] = 0;
        registry.Values[x64] = 0;
        ManagerState state = CreateState(Path.Combine(directory, "central"));
        state.VulkanBuild = RuntimeBuild.FullAddon;
        string borrowed = Path.Combine(directory, "already-listed.exe");
        string added = Path.Combine(directory, "new-managed.exe");
        string external = Path.Combine(directory, "other-app.exe");
        state.Games.Add(new GameProfile { Executable = borrowed, Api = RenderApi.Vulkan,
            Architecture = CpuArchitecture.X64 });
        state.Games.Add(new GameProfile { Executable = added, Api = RenderApi.Vulkan,
            Architecture = CpuArchitecture.X64 });
        string apps = Path.Combine(shared, "ReShadeApps.ini");
        File.WriteAllText(apps, "; Keep this comment\nApps=" + IniDocument.Join([external, borrowed]) +
            "\n[Custom]\nKeep=42\n");
        Dictionary<string, string> hashes = Directory.GetFiles(shared).Where(path => path != apps)
            .ToDictionary(path => path, FileTransaction.Hash);
        var service = new VulkanService(registry);
        using (var transaction = new FileTransaction(Path.Combine(directory, "recovery"), "use native layer", registry))
        {
            service.Enable(state, transaction);
            transaction.Commit();
        }

        AssertTrue(registry.WriteHistory.Count == 0 && registry.Values.Count == 2);
        AssertTrue(state.VulkanLayerFiles.Count == 0 && state.OwnedVulkanApps.Single().Executable == added);
        AssertTrue(hashes.All(item => FileTransaction.Hash(item.Key) == item.Value));
        AssertTrue(!Directory.Exists(state.VulkanLayerDirectory!));
        AssertTrue(service.Inspect(state).IsApplied);
        registry.Values[x86] = 1;
        using (var transaction = new FileTransaction(Path.Combine(directory, "recovery"), "repair", registry))
        {
            service.Enable(state, transaction);
            transaction.Commit();
        }

        AssertTrue(registry.WriteHistory.Count == 0 && registry.Read(x86) == 1);
        AssertTrue(!service.Inspect(state).IsApplied);
        state.Games.Clear();
        using (var transaction = new FileTransaction(Path.Combine(directory, "recovery"), "unregister", registry))
        {
            service.WriteApps(state, transaction, enroll: false);
            transaction.Commit();
        }

        AssertTrue(IniDocument.Split(IniDocument.Read(apps).Get("", "Apps")).SequenceEqual([external, borrowed]));
        AssertTrue(File.ReadAllText(apps).Contains("; Keep this comment") &&
            IniDocument.Read(apps).Get("Custom", "Keep") == "42");
        AssertTrue(registry.WriteHistory.Count == 0 &&
            hashes.All(item => FileTransaction.Hash(item.Key) == item.Value));
        return Task.CompletedTask;
    }

    private static async Task OwnedVulkanLayerSurvivesRepairUpdatesAndLastGameAsync()
    {
        string directory = CreateTestDirectory("persistent-owned-vulkan");
        var store = new StateStore(Path.Combine(directory, "manager"));
        var registry = new InMemoryLayerRegistry();
        using var manager = new Manager(store, registry);
        await manager.InitializeAsync(Path.Combine(directory, "central"), None);
        string standard = Path.Combine(repositoryRoot, ".validation", "ReShade_Setup_6.8.0.exe");
        string addon = Path.Combine(repositoryRoot, ".validation", "ReShade_Setup_6.8.0_Addon.exe");
        await manager.ImportRuntimeAsync(standard, addon, None);
        GameProfile game = CreateRegistrationGame(manager.State!, Path.Combine(directory, "game"));
        game.Api = RenderApi.Vulkan;
        await manager.InstallAsync(game, None);
        Dictionary<LayerRegistration, int> original = registry.Values.ToDictionary();
        AssertTrue(original.Count == 2 && manager.State!.OwnedVulkanRegistrations.Count == 2);
        string layerDirectory = manager.State!.VulkanLayerDirectory!;
        string apps = Path.Combine(layerDirectory, "ReShadeApps.ini");
        string external = Path.Combine(directory, "external-app.exe");
        var document = IniDocument.Read(apps);
        document.Set("", "Apps", IniDocument.Join([game.Executable!, external]));
        document.Set("Custom", "Keep", "unchanged");
        File.WriteAllText(apps, document.ToString());
        registry.WriteHistory.Clear();
        await manager.RepairAsync(null, None);
        await manager.SetVulkanBuildAsync(RuntimeBuild.FullAddon, None);
        AssertTrue(FileTransaction.Hash(Path.Combine(layerDirectory, "ReShade64.dll")) ==
            FileTransaction.Hash(Path.Combine(manager.State.Paths.AddonBinaries, "ReShade64.dll")));
        AssertTrue(registry.WriteHistory.Count == 0);
        await manager.ImportRuntimeAsync(standard, addon, None);
        AssertTrue(manager.State.VulkanLayerDirectory == layerDirectory && registry.WriteHistory.Count == 0);
        AssertTrue(original.All(item => registry.Read(item.Key) == item.Value));
        AssertTrue(registry.Values.Count == original.Count);

        // Even an externally broken layer must not block detaching a game or be removed as cleanup.
        string changed = original.Keys.First().Manifest;
        File.WriteAllText(changed, "externally modified manifest");
        await manager.UnregisterAsync(game.Id, None);
        AssertTrue(manager.State.Games.Count == 0 && manager.State.VulkanManaged);
        AssertTrue(registry.WriteHistory.Count == 0 && original.All(item => registry.Read(item.Key) == item.Value));
        AssertTrue(File.ReadAllText(changed) == "externally modified manifest");
        AssertTrue(File.Exists(Path.Combine(layerDirectory, "ReShade64.dll")));
        AssertTrue(IniDocument.Split(IniDocument.Read(apps).Get("", "Apps")).Single() == external);
        AssertTrue(IniDocument.Read(apps).Get("Custom", "Keep") == "unchanged");
        AssertTrue(!FileTransaction.Exists(Path.Combine(Path.GetDirectoryName(game.Executable)!, "ReShade.ini")));
    }


}
