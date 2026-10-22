using System.Text.Json.Nodes;

namespace ReShadeCentralized.Tests;

internal static partial class TestSuite
{
    private static async Task ExecutableNamesChangeWithoutMovingProfilesAsync()
    {
        string directory = CreateTestDirectory("executable-names");
        var store = new StateStore(Path.Combine(directory, "manager"));
        using var manager = new Manager(store, new InMemoryLayerRegistry());
        await manager.InitializeAsync(Path.Combine(directory, "central"), null, None);
        await manager.ImportRuntimeAsync(
            Path.Combine(repositoryRoot, ".validation", "ReShade_Setup_6.8.0.exe"),
            Path.Combine(repositoryRoot, ".validation", "ReShade_Setup_6.8.0_Addon.exe"),
            None);
        string folder = Path.Combine(directory, "game");
        Directory.CreateDirectory(folder);
        string executable = Path.Combine(folder, "game.exe");
        string replacement = Path.Combine(folder, "updated-game.exe");
        string runtime = Path.Combine(runtimePayload, "Standard", "ReShade64.dll");
        File.Copy(runtime, executable);
        File.Copy(runtime, replacement);
        await manager.InstallAsync(new GameProfile { Executable = executable, Api = RenderApi.DXGI }, None);
        GameProfile installed = manager.State!.Games.Single();
        AssertTrue(installed.Name == "game.exe" && store.Load()!.Games.Single().Name == "game.exe");
        string id = installed.Id;
        string profileDirectory = installed.Directory;
        string preset = Path.Combine(profileDirectory, "ReShadePreset.ini");
        File.WriteAllText(preset, "Custom preset contents");

        GameProfile request = manager.State.Clone().Games.Single();
        request.Name = "Ignored custom name";
        request.Executable = replacement;
        await manager.InstallAsync(request, None);
        installed = manager.State.Games.Single();
        AssertTrue(installed.Name == "updated-game.exe" && installed.Id == id);
        AssertTrue(installed.Directory == profileDirectory && File.ReadAllText(preset) == "Custom preset contents");
        AssertTrue(store.Load()!.Games.Single().Name == "updated-game.exe");
        AssertTrue(installed.Links.All(DeploymentService.IsOwnedLink));
    }

    private static Task UnmanagedInstallationDetectionPreservesBackupsAsync()
    {
        string directory = CreateTestDirectory("unmanaged-installation");
        ManagerState state = CreateState(Path.Combine(directory, "central"));
        string backups = Path.Combine(directory, "backups");
        DeployRuntimePayload(state, backups, new InMemoryLayerRegistry());
        string gameDirectory = Path.Combine(directory, "game");
        Directory.CreateDirectory(Path.Combine(gameDirectory, "reshade-shaders", "Shaders"));
        string localShader = Path.Combine(gameDirectory, "reshade-shaders", "Shaders", "Custom.fx");
        File.WriteAllText(localShader, "// custom shader");
        string executable = Path.Combine(gameDirectory, "game.exe");
        string proxy = Path.Combine(gameDirectory, "dxgi.dll");
        string runtime = Path.Combine(state.Paths.StandardBinaries, "ReShade64.dll");
        File.Copy(runtime, executable);
        File.Copy(runtime, proxy);
        string config = Path.Combine(gameDirectory, "ReShade.ini");
        const string originalConfig = "[GENERAL]\nEffectSearchPaths=.\\reshade-shaders\\Shaders\n";
        File.WriteAllText(config, originalConfig);
        string proxyHash = FileTransaction.Hash(proxy);
        ExistingInstallationNotice? notice = DeploymentService.InspectExistingInstallation(
            executable, RenderApi.DXGI, []);
        AssertTrue(notice != null && !notice.BlocksInstallation);
        AssertTrue(notice!.Message.Contains("dxgi.dll") && notice.Message.Contains("ReShade.ini") &&
            notice.Message.Contains("reshade-shaders"));

        notice = DeploymentService.InspectExistingInstallation(executable, RenderApi.OpenGL, []);
        AssertTrue(notice!.BlocksInstallation && notice.Message.Contains("matching Graphics API"));
        var game = new GameProfile
        {
            Name = "Existing ReShade",
            Executable = executable,
            Api = RenderApi.DXGI,
            Directory = Path.Combine(state.Paths.Profiles, "existing")
        };
        string backup;
        using (var transaction = new FileTransaction(backups, "Adopt unmanaged ReShade"))
        {
            backup = transaction.BackupDirectory;
            DeploymentService.Install(state, game, transaction);
            transaction.Commit();
        }

        AssertTrue(game.Links.Count == 2 && game.Links.All(DeploymentService.IsOwnedLink));
        AssertTrue(DeploymentService.InspectExistingInstallation(executable, RenderApi.DXGI, game.Links) == null);
        AssertTrue(File.ReadAllText(localShader) == "// custom shader");
        File.Delete(proxy);
        File.Copy(runtime, proxy);
        notice = DeploymentService.InspectExistingInstallation(executable, RenderApi.DXGI, game.Links);
        AssertTrue(notice!.BlocksInstallation && notice.Message.Contains("changed outside this app"));
        FileTransaction.Restore(backup, null);
        AssertTrue(FileTransaction.Link(proxy) == null && FileTransaction.Hash(proxy) == proxyHash);
        AssertTrue(FileTransaction.Link(config) == null && File.ReadAllText(config) == originalConfig);
        AssertTrue(File.Exists(localShader));
        return Task.CompletedTask;
    }

    private static Task BuildIdentityAndVulkanManifestTraversalValidationAsync()
    {
        AssertThrows<InvalidDataException>(() => RuntimeService.ValidatePayload(
                Path.Combine(runtimePayload, "Standard"),
                RuntimeBuild.FullAddon));
        AssertThrows<InvalidDataException>(() => RuntimeService.ValidatePayload(
                Path.Combine(runtimePayload, "FullAddon"),
                RuntimeBuild.Standard));
        string directory = CreateTestDirectory("bad-manifest");
        foreach (string file in Directory.GetFiles(Path.Combine(runtimePayload, "Standard")))
        {
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        }

        string json = Path.Combine(directory, "ReShade64.json");
        JsonNode node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(json))!;
        node["layer"]!["library_path"] = "../ReShade64.dll";
        File.WriteAllText(json, node.ToJsonString());
        AssertThrows<InvalidDataException>(() => RuntimeService.ValidatePayload(directory));
        string dll = Path.Combine(directory, "ReShade64.dll");
        byte[] bytes = File.ReadAllBytes(dll);
        int pe = BitConverter.ToInt32(bytes, 0x3c);
        bytes[pe + 4] = 0x64;
        bytes[pe + 5] = 0xaa;
        File.WriteAllBytes(dll, bytes);
        AssertThrows<NotSupportedException>(() => PortableExecutable.Architecture(dll));
        return Task.CompletedTask;
    }

    private static Task VulkanMigratesBothViewsAndExternalApplicationsAsync()
    {
        string directory = CreateTestDirectory("vulkan");
        string backups = Path.Combine(directory, "backups");
        ManagerState state = CreateState(Path.Combine(directory, "central"));
        var registry = new InMemoryLayerRegistry();
        DeployRuntimePayload(state, backups, registry);
        string old = Path.Combine(directory, "external");
        Directory.CreateDirectory(old);
        foreach (string file in Directory.GetFiles(Path.Combine(runtimePayload, "Standard")))
        {
            File.Copy(file, Path.Combine(old, Path.GetFileName(file)));
        }

        File.WriteAllText(Path.Combine(old, "ReShadeApps.ini"), "Apps=C:\\games\\one.exe,C:\\games\\two.exe\n");
        var old64 = new LayerRegistration("HKLM", "64", Path.Combine(old, "ReShade64.json"));
        var old32 = new LayerRegistration("HKLM", "32", Path.Combine(old, "ReShade32.json"));
        registry.Values[old64] = 0;
        registry.Values[old32] = 0;
        var service = new VulkanService(registry);
        string backup;
        using (var transaction = new FileTransaction(backups, "migrate", registry))
        {
            backup = transaction.BackupDirectory;
            service.Enable(state, transaction);
            transaction.Commit();
        }

        AssertTrue(state.ExternalVulkanApps.Count == 2 && state.OwnedRegistrations.Count == 2 &&
            registry.Read(old64) == null &&
            registry.Read(old32) == null);
        AssertTrue(registry.Values.Keys.All(registration =>
            registration.Manifest.StartsWith(state.Paths.StandardBinaries)));
        using (var transaction = new FileTransaction(backups, "switch", registry))
        {
            state.VulkanBuild = RuntimeBuild.FullAddon;
            service.Enable(state, transaction);
        }

        AssertTrue(registry.Values.Keys.All(registration =>
            registration.Manifest.StartsWith(state.Paths.StandardBinaries)));
        FileTransaction.Restore(backup, registry);
        AssertTrue(registry.Values.Count == 2 && registry.Read(old64) == 0 && registry.Read(old32) == 0);
        return Task.CompletedTask;
    }

    private static Task VulkanRemovalRetainsSharedLayerAndUnownedFilesAsync()
    {
        string directory = CreateTestDirectory("vulkan-refcount");
        var registry = new InMemoryLayerRegistry();
        ManagerState state = CreateState(Path.Combine(directory, "central"));
        DeployRuntimePayload(state, Path.Combine(directory, "backups"), registry);
        var other = new LayerRegistration("HKCU", "64", Path.Combine(directory, "other.json"));
        registry.Values[other] = 1;
        var service = new VulkanService(registry);
        using (var transaction = new FileTransaction(Path.Combine(directory, "backups"), "enable", registry))
        {
            service.Enable(state, transaction);
            transaction.Commit();
        }

        state.ExternalVulkanApps.Add(@"C:\keep.exe");
        using (var transaction = new FileTransaction(Path.Combine(directory, "backups"), "retain", registry))
        {
            service.RemoveIfUnused(state, transaction);
            transaction.Commit();
        }

        AssertTrue(state.VulkanManaged && registry.Values.Count == 3);
        state.ExternalVulkanApps.Clear();
        using (var transaction = new FileTransaction(Path.Combine(directory, "backups"), "remove", registry))
        {
            service.RemoveIfUnused(state, transaction);
            transaction.Commit();
        }

        AssertTrue(!state.VulkanManaged && registry.Values.Count == 1 && registry.Read(other) == 1);
        return Task.CompletedTask;
    }

    private static Task VulkanRepairPreservesNewlyCompetingInstallerConsumersAsync()
    {
        string directory = CreateTestDirectory("vulkan-repair");
        string backups = Path.Combine(directory, "backups");
        var registry = new InMemoryLayerRegistry();
        ManagerState state = CreateState(Path.Combine(directory, "central"));
        DeployRuntimePayload(state, backups, registry);
        var service = new VulkanService(registry);
        using (var transaction = new FileTransaction(backups, "enable", registry))
        {
            service.Enable(state, transaction);
            transaction.Commit();
        }

        string competing = Path.Combine(directory, "competing");
        Directory.CreateDirectory(competing);
        foreach (string file in Directory.GetFiles(Path.Combine(runtimePayload, "Standard")))
        {
            File.Copy(file, Path.Combine(competing, Path.GetFileName(file)));
        }

        File.WriteAllText(Path.Combine(competing, "ReShadeApps.ini"), @"Apps=C:\new-game.exe");
        var entry = new LayerRegistration("HKCU", "64", Path.Combine(competing, "ReShade64.json"), "Explicit");
        registry.Values[entry] = 0;
        using (var transaction = new FileTransaction(backups, "repair", registry))
        {
            service.Enable(state, transaction);
            transaction.Commit();
        }

        AssertTrue(state.ExternalVulkanApps.Contains(@"C:\new-game.exe") && registry.Read(entry) == null &&
            registry.Values.Count == 2);
        return Task.CompletedTask;
    }

    private static Task LockedRuntimeUpdatePreservesPreviousInstallationAsync()
    {
        string directory = CreateTestDirectory("locked");
        string backups = Path.Combine(directory, "backups");
        ManagerState state = CreateState(Path.Combine(directory, "central"));
        DeployRuntimePayload(state, backups);
        var original = state.RuntimeHashes.ToDictionary();
        string locked = Path.Combine(state.Paths.AddonBinaries, "ReShade64.dll");
        using var handle = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read);
        AssertThrows<IOException>(() =>
        {
            using var transaction = new FileTransaction(backups, "locked runtime");
            RuntimeService.Deploy(runtimePayload, state, transaction, None);
            transaction.Commit();
        });
        AssertTrue(original.All(item => FileTransaction.Hash(item.Key) == item.Value));
        return Task.CompletedTask;
    }

    private static Task GameLinksSupportRepairApiChangesAndRemovalAsync()
    {
        string directory = CreateTestDirectory("links");
        string backups = Path.Combine(directory, "backups");
        ManagerState state = CreateState(Path.Combine(directory, "central"));
        DeployRuntimePayload(state, backups);
        string gameDir = Path.Combine(directory, "game");
        Directory.CreateDirectory(gameDir);
        string executablePath = Path.Combine(gameDir, "game.exe");
        File.Copy(Path.Combine(runtimePayload, "Standard", "ReShade64.dll"), executablePath);
        var game = new GameProfile
        {
            Name = "Test",
            Executable = executablePath,
            Api = RenderApi.DXGI,
            Directory = Path.Combine(state.Paths.Profiles, "test")
        };
        state.Games.Add(game);
        using (var transaction = new FileTransaction(backups, "install"))
        {
            DeploymentService.Install(state, game, transaction);
            transaction.Commit();
        }

        AssertTrue(game.Links.Count == 2 && game.Links.All(DeploymentService.IsOwnedLink));
        using (var transaction = new FileTransaction(backups, "repair"))
        {
            DeploymentService.Install(state, game, transaction);
            transaction.Commit();
        }

        string proxy = Path.Combine(gameDir, "dxgi.dll");
        File.Delete(proxy);
        File.WriteAllText(proxy, "unrelated");
        AssertThrows<IOException>(() =>
        {
            using var transaction = new FileTransaction(backups, "ownership");
            DeploymentService.Install(state, game, transaction);
        });
        AssertTrue(File.ReadAllText(proxy) == "unrelated");
        File.Delete(proxy);
        using (var transaction = new FileTransaction(backups, "repair missing"))
        {
            DeploymentService.Install(state, game, transaction);
            transaction.Commit();
        }

        game.Api = RenderApi.Vulkan;
        using (var transaction = new FileTransaction(backups, "switch"))
        {
            DeploymentService.Install(state, game, transaction);
            transaction.Commit();
        }

        AssertTrue(!File.Exists(proxy) && game.Links.Count == 1);
        using (var transaction = new FileTransaction(backups, "remove"))
        {
            DeploymentService.Remove(game, transaction);
            transaction.Commit();
        }

        AssertTrue(!FileTransaction.Exists(Path.Combine(gameDir, "ReShade.ini")) &&
            File.Exists(ConfigurationService.ConfigPath(game)));
        return Task.CompletedTask;
    }

    private static async Task ManagerRelocationRepairsLinksAndPreservesOriginalDataAsync()
    {
        string directory = CreateTestDirectory("manager-relocate");
        var store = new StateStore(directory);
        var registry = new InMemoryLayerRegistry();
        using var manager = new Manager(store, registry);
        await manager.InitializeAsync(Path.Combine(directory, "central"), null, None);
        await manager.ImportRuntimeAsync(
            Path.Combine(repositoryRoot, ".validation", "ReShade_Setup_6.8.0.exe"),
            Path.Combine(repositoryRoot, ".validation", "ReShade_Setup_6.8.0_Addon.exe"),
            None);
        string gameDir = Path.Combine(directory, "game");
        Directory.CreateDirectory(gameDir);
        string executablePath = Path.Combine(gameDir, "game.exe");
        File.Copy(Path.Combine(runtimePayload, "Standard", "ReShade64.dll"), executablePath);
        await manager.InstallAsync(new()
        {
            Name = "Relocation",
            Executable = executablePath,
            Api = RenderApi.DXGI
        }, None);
        string old = manager.State!.Paths.Root;
        await manager.RelocateAsync(Path.Combine(directory, "destination"), None);
        AssertTrue(manager.State.Games.Single().Links.All(DeploymentService.IsOwnedLink));
        AssertTrue(FileTransaction.Hash(Path.Combine(
                    manager.State.Paths.StandardBinaries,
                    "ReShade64.dll")) == manager.State.RuntimeHashes[Path.Combine(
                manager.State.Paths.StandardBinaries,
                "ReShade64.dll")]);
        string relocation = Directory.GetDirectories(store.Backups).OrderDescending().First();
        await manager.RestoreToAsync(relocation, None);
        AssertTrue(manager.State!.Paths.Root == old &&
            manager.State.Games.Single().Links.All(DeploymentService.IsOwnedLink));
    }

    private static Task GameLinksTargetCorrectArchitecturesAndBuildsAsync()
    {
        string directory = CreateTestDirectory("architectures");
        string backups = Path.Combine(directory, "backups");
        ManagerState state = CreateState(Path.Combine(directory, "central"));
        DeployRuntimePayload(state, backups);
        foreach (CpuArchitecture architecture in Enum.GetValues<CpuArchitecture>())
        {
            string folder = Path.Combine(directory, architecture.ToString());
            Directory.CreateDirectory(folder);
            string executablePath = Path.Combine(folder, "game.exe");
            string stem = architecture == CpuArchitecture.X86 ? "ReShade32.dll" : "ReShade64.dll";
            File.Copy(Path.Combine(runtimePayload, "Standard", stem), executablePath);
            var game = new GameProfile
            {
                Executable = executablePath,
                Name = architecture.ToString(),
                Api = RenderApi.OpenGL,
                Directory = Path.Combine(state.Paths.Profiles, architecture.ToString())
            };
            foreach (RuntimeBuild build in Enum.GetValues<RuntimeBuild>())
            {
                game.Build = build;
                using var transaction = new FileTransaction(backups, "install architecture");
                DeploymentService.Install(state, game, transaction);
                transaction.Commit();
                AssertTrue(game.Architecture == architecture && game.Links.All(DeploymentService.IsOwnedLink));
                AssertTrue(FileTransaction.Same(
                        new FileInfo(Path.Combine(folder, "opengl32.dll")).ResolveLinkTarget(true)!.FullName,
                        Path.Combine(state.Paths.Binaries(build), stem)));
            }
        }

        return Task.CompletedTask;
    }


}
