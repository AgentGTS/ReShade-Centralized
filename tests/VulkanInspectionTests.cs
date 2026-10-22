using System.Text.Json;

namespace ReShadeCentralized.Tests;

internal static partial class TestSuite
{
    private static Task VulkanInspectionVerifiesRegistrationWithoutChangesAsync()
    {
        string directory = CreateTestDirectory("vulkan-inspection");
        string backups = Path.Combine(directory, "backups");
        ManagerState state = CreateState(Path.Combine(directory, "central"));
        var registry = new InMemoryLayerRegistry();
        var service = new VulkanService(registry);
        DeployRuntimePayload(state, backups, registry);
        AssertTrue(!service.Inspect(state).IsApplied && registry.Values.Count == 0);

        string managed = Path.Combine(directory, "managed.exe");
        string external = Path.Combine(directory, "external.exe");
        string directX = Path.Combine(directory, "directx.exe");
        state.Games.Add(new GameProfile { Executable = managed, Api = RenderApi.Vulkan });
        state.Games.Add(new GameProfile { Executable = directX, Api = RenderApi.DXGI });
        state.ExternalVulkanApps.AddRange([external, managed.ToUpperInvariant(), directX]);
        using (var transaction = new FileTransaction(backups, "apply", registry))
        {
            service.Enable(state, transaction);
            transaction.Commit();
        }

        VulkanInspection applied = service.Inspect(state);
        AssertTrue(applied.IsApplied && applied.Error == null);
        AssertTrue(applied.Consumers.Count == 2 && applied.Consumers.Contains(managed) &&
            applied.Consumers.Contains(external));
        LayerRegistration registration = state.OwnedRegistrations.Single(item => item.View == "32");
        registry.Values.Remove(registration);
        AssertTrue(!service.Inspect(state).IsApplied);
        registry.Values[registration] = 1;
        AssertTrue(!service.Inspect(state).IsApplied);
        registry.Values[registration] = 0;
        AssertTrue(service.Inspect(state).IsApplied);
        state.VulkanBuild = RuntimeBuild.FullAddon;
        AssertTrue(!service.Inspect(state).IsApplied);
        state.VulkanBuild = RuntimeBuild.Standard;

        string originalManifest = File.ReadAllText(registration.Manifest);
        File.WriteAllText(registration.Manifest, "not JSON");
        AssertTrue(!service.Inspect(state).IsApplied && service.Inspect(state).Error != null);
        File.WriteAllText(registration.Manifest, originalManifest);

        string competingDirectory = Path.Combine(directory, "competing");
        Directory.CreateDirectory(competingDirectory);
        foreach (string file in Directory.GetFiles(Path.Combine(runtimePayload, "Standard")))
        {
            File.Copy(file, Path.Combine(competingDirectory, Path.GetFileName(file)));
        }

        string discovered = Path.Combine(directory, "discovered.exe");
        string appsPath = Path.Combine(competingDirectory, "ReShadeApps.ini");
        File.WriteAllText(appsPath, "Apps=" + IniDocument.Join([discovered, external, directX]));
        var competitor = new LayerRegistration("HKCU", "64", Path.Combine(competingDirectory, "ReShade64.json"));
        registry.Values[competitor] = 0;
        string stateBefore = JsonSerializer.Serialize(state, ManagerState.Json);
        string appsHash = FileTransaction.Hash(appsPath);
        VulkanInspection conflicting = service.Inspect(state);
        AssertTrue(!conflicting.IsApplied && conflicting.Message.Contains("Another ReShade"));
        AssertTrue(conflicting.Consumers.Count == 3 && conflicting.Consumers.Contains(discovered));
        AssertTrue(JsonSerializer.Serialize(state, ManagerState.Json) == stateBefore);
        AssertTrue(registry.Values.Count == 3 && registry.Values[competitor] == 0);
        AssertTrue(FileTransaction.Hash(appsPath) == appsHash);
        using (var transaction = new FileTransaction(backups, "repair", registry))
        {
            service.Enable(state, transaction);
            transaction.Commit();
        }

        AssertTrue(service.Inspect(state).IsApplied && service.Inspect(state).Consumers.Contains(discovered));
        return Task.CompletedTask;
    }

    private static async Task VulkanInventoryFollowsGameAndRegistrationChangesAsync()
    {
        string directory = CreateTestDirectory("vulkan-inventory");
        var store = new StateStore(Path.Combine(directory, "manager"));
        var registry = new InMemoryLayerRegistry();
        using var manager = new Manager(store, registry);
        await manager.InitializeAsync(Path.Combine(directory, "central"), null, None);
        await manager.ImportRuntimeAsync(
            Path.Combine(repositoryRoot, ".validation", "ReShade_Setup_6.8.0.exe"),
            Path.Combine(repositoryRoot, ".validation", "ReShade_Setup_6.8.0_Addon.exe"), None);
        string gameDirectory = Path.Combine(directory, "game");
        Directory.CreateDirectory(gameDirectory);
        string executable = Path.Combine(gameDirectory, "game.exe");
        File.Copy(Path.Combine(runtimePayload, "Standard", "ReShade64.dll"), executable);
        var game = new GameProfile { Executable = executable, Api = RenderApi.Vulkan };
        await manager.InstallAsync(game, None);
        AssertTrue(manager.InspectVulkan().IsApplied && manager.InspectVulkan().Consumers.Single() == executable);
        using (var reopened = new Manager(store, registry))
        {
            reopened.Open();
            AssertTrue(reopened.InspectVulkan().IsApplied &&
                reopened.InspectVulkan().Consumers.Single() == executable);
        }

        await manager.SetVulkanBuildAsync(RuntimeBuild.FullAddon, None);
        AssertTrue(manager.InspectVulkan().IsApplied && manager.InspectVulkan().Consumers.Single() == executable);
        await manager.RemoveAsync(game.Id, None);
        AssertTrue(!manager.InspectVulkan().IsApplied && manager.InspectVulkan().Consumers.Count == 0);

        await manager.SetVulkanBuildAsync(RuntimeBuild.Standard, None);
        string first = Path.Combine(directory, "first-external.exe");
        string second = Path.Combine(directory, "second-external.exe");
        string apps = Path.Combine(manager.State!.Paths.StandardBinaries, "ReShadeApps.ini");
        File.WriteAllText(apps, "Apps=" + IniDocument.Join([first, second]));
        AssertTrue(manager.InspectVulkan().Consumers.Count == 2);
        File.WriteAllText(apps, "Apps=" + IniDocument.Join([second]));
        AssertTrue(manager.InspectVulkan().IsApplied && manager.InspectVulkan().Consumers.Single() == second);
        File.WriteAllText(apps, "Apps=");
        AssertTrue(manager.InspectVulkan().IsApplied && manager.InspectVulkan().Consumers.Count == 0);
    }
}
