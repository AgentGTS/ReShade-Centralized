namespace ReShadeCentralized.Tests;

internal static partial class TestSuite
{
    private static async Task<(Manager Manager, InMemoryLayerRegistry Registry)> CreateVulkanRemovalFixtureAsync()
    {
        string root = CreateTestDirectory("vulkan-removal");
        var registry = new InMemoryLayerRegistry();
        var manager = new Manager(new StateStore(root), registry);
        await manager.InitializeAsync(Path.Combine(root, "central"), None);
        ManagerState state = manager.State!;
        state.VulkanManaged = true;
        state.VulkanLayerDirectory = Path.Combine(state.Paths.Root, "Vulkan");
        Directory.CreateDirectory(state.VulkanLayerDirectory);
        foreach (string view in new[] { "32", "64" })
        {
            var registration = new LayerRegistration("HKLM", view,
                Path.Combine(state.VulkanLayerDirectory, "ReShade" + view + ".json"));
            foreach (string extension in new[] { ".json", ".dll" })
            {
                string file = Path.ChangeExtension(registration.Manifest, extension);
                File.WriteAllText(file, "Owned removal fixture " + view + extension);
                state.VulkanLayerFiles[file] = FileTransaction.Hash(file);
            }
            state.OwnedVulkanRegistrations.Add(registration);
            registry.Values[registration] = 0;
        }
        using var transaction = new FileTransaction(manager.Store.Recovery, "save fixture", registry);
        manager.Store.Save(state, transaction);
        transaction.Commit();
        return (manager, registry);
    }

    private static async Task VulkanRemovalPreservesOtherRegistrationsAndFilesAsync()
    {
        (Manager manager, InMemoryLayerRegistry registry) = await CreateVulkanRemovalFixtureAsync();
        using (manager)
        {
            ManagerState state = manager.State!;
            LayerRegistration[] owned = state.OwnedVulkanRegistrations.ToArray();
            LayerRegistration[] foreign =
            [
                new("HKLM", "64", Path.Combine(state.Paths.Root, "native", "ReShade64.json")),
                new("HKCU", "64", owned[1].Manifest),
                new("HKLM", "32", Path.Combine(state.Paths.Root, "overlay.json"), "Explicit")
            ];
            foreach (LayerRegistration entry in foreign)
            {
                registry.Values[entry] = 0;
            }
            Dictionary<string, string> files = state.VulkanLayerFiles.ToDictionary();
            VulkanRemovalResult result = await manager.RemoveVulkanSupportAsync(None);
            AssertTrue(result.Removed.Count == 2 && result.Preserved.Count == 0);
            AssertTrue(foreign.All(entry => registry.Read(entry) == 0) && registry.Values.Count == foreign.Length);
            AssertTrue(registry.WriteHistory.All(write => owned.Contains(write.Registration) && write.Value == null));
            AssertTrue(files.All(file => File.Exists(file.Key) && FileTransaction.Hash(file.Key) == file.Value));
            AssertTrue(manager.State!.OwnedVulkanRegistrations.Count == 0 && !manager.State.VulkanManaged);
            manager.Open();
            AssertTrue(manager.State!.OwnedVulkanRegistrations.Count == 0);
        }
    }

    private static async Task LastVulkanUnregisterPreservesStandaloneConsumersAsync()
    {
        (Manager manager, InMemoryLayerRegistry registry) = await CreateVulkanRemovalFixtureAsync();
        using (manager)
        {
            ManagerState state = manager.State!;
            string gameDirectory = Path.Combine(state.Paths.Root, "test-game");
            Directory.CreateDirectory(gameDirectory);
            string executable = Path.Combine(gameDirectory, "game.exe");
            File.WriteAllText(executable, "Registration fixture");
            string profile = Path.Combine(state.Paths.Profiles, "game_fixture");
            Directory.CreateDirectory(profile);
            string config = Path.Combine(profile, "ReShade.ini");
            File.WriteAllText(config, "[GENERAL]\n");
            string localConfig = Path.Combine(gameDirectory, "ReShade.ini");
            File.CreateSymbolicLink(localConfig, config);
            var game = new GameProfile { Executable = executable, Directory = profile, Api = RenderApi.Vulkan,
                Links = [new(localConfig, config)] };
            state.Games.Add(game);
            string external = Path.Combine(state.Paths.Root, "standalone-game.exe");
            string apps = Path.Combine(state.VulkanLayerDirectory!, "ReShadeApps.ini");
            File.WriteAllText(apps, "; Native installer entries must survive\nApps=" +
                IniDocument.Join([executable, external]) + "\n[Custom]\nKeep=42\n");
            state.OwnedVulkanApps.Add(new(apps, executable));
            await AssertThrowsAsync<InvalidOperationException>(() => manager.RemoveVulkanSupportAsync(None));
            await manager.UnregisterAsync(game.Id, None);
            AssertTrue(registry.WriteHistory.Count == 0 && registry.Values.Count == 2);
            AssertTrue(!FileTransaction.Exists(localConfig) && File.Exists(config));
            AssertTrue(IniDocument.Split(IniDocument.Read(apps).Get("", "Apps")).SequenceEqual([external]));
            AssertTrue(File.ReadAllText(apps).Contains("; Native installer entries must survive") &&
                IniDocument.Read(apps).Get("Custom", "Keep") == "42");
            await AssertThrowsAsync<InvalidOperationException>(() => manager.RemoveVulkanSupportAsync(None));
            AssertTrue(registry.WriteHistory.Count == 0 && registry.Values.Count == 2);
        }
    }

    private static async Task VulkanRemovalPreservesChangedAndUnownedEntriesAsync()
    {
        foreach (string variant in new[] { "disabled", "changed-file", "linked-file", "legacy", "foreign-hive" })
        {
            (Manager manager, InMemoryLayerRegistry registry) = await CreateVulkanRemovalFixtureAsync();
            using (manager)
            {
                ManagerState state = manager.State!;
                LayerRegistration target = state.OwnedVulkanRegistrations[0];
                switch (variant)
                {
                    case "disabled":
                        registry.Values[target] = 1;
                        break;
                    case "changed-file":
                        File.AppendAllText(target.Manifest, "External changes");
                        break;
                    case "linked-file":
                        File.Delete(target.Manifest);
                        string external = Path.Combine(state.Paths.Root, "external-layer.json");
                        File.WriteAllText(external, "External layer");
                        File.CreateSymbolicLink(target.Manifest, external);
                        break;
                    case "legacy":
                        state.OwnedVulkanRegistrations.Clear();
                        break;
                    case "foreign-hive":
                        registry.Values.Remove(target);
                        target = target with { Hive = "HKCU" };
                        registry.Values[target] = 0;
                        state.OwnedVulkanRegistrations[0] = target;
                        break;
                }
                int originalValue = registry.Values[target];
                VulkanRemovalResult result = await manager.RemoveVulkanSupportAsync(None);
                AssertTrue(registry.Read(target) == originalValue);
                AssertTrue(!registry.WriteHistory.Any(write => write.Registration == target));
                AssertTrue(variant == "legacy" ? result.Removed.Count == 0 :
                    result.Removed.Count == 1 && result.Preserved.Count == 1);
            }
        }
    }

    private static async Task VulkanRemovalRollsBackAndRecoversRegistryChangesAsync()
    {
        (Manager manager, InMemoryLayerRegistry registry) = await CreateVulkanRemovalFixtureAsync();
        using (manager)
        {
            LayerRegistration[] original = registry.Values.Keys.ToArray();
            string originalState = File.ReadAllText(manager.Store.StatePath);
            registry.FailWrite = 2;
            await AssertThrowsAsync<IOException>(() => manager.RemoveVulkanSupportAsync(None));
            AssertTrue(original.All(entry => registry.Read(entry) == 0));
            AssertTrue(File.ReadAllText(manager.Store.StatePath) == originalState &&
                manager.State!.OwnedVulkanRegistrations.Count == 2);
            registry.FailWrite = null;
            var interrupted = new FileTransaction(manager.Store.Recovery, "interrupted removal", registry);
            new VulkanService(registry).RemoveOwnedRegistrations(manager.State!.Clone(), interrupted);
            AssertTrue(registry.Values.Count == 0);
            FileTransaction.RecoverPending(manager.Store.Recovery, registry);
            AssertTrue(original.All(entry => registry.Read(entry) == 0));
            AssertTrue(File.ReadAllText(manager.Store.StatePath) == originalState);
        }
    }
}
