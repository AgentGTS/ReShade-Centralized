namespace ReShadeCentralized.Tests;

internal static partial class TestSuite
{
    private static async Task CompatibilityDeploymentSurvivesRepairAndRestartAsync()
    {
        (string Executable, bool Is64Bit, RenderApi Api,
            string Folder, string Proxy, CpuArchitecture Architecture)[] cases =
        [
            ("bms.exe", false, RenderApi.Direct3D9, "bin", "d3d9.dll", CpuArchitecture.X86),
            ("RDR2.exe", true, RenderApi.DXGI, "", "d3d12.dll", CpuArchitecture.X64),
            ("RDR2.exe", true, RenderApi.OpenGL, "", "opengl32.dll", CpuArchitecture.X64),
            ("Dead Space.exe", false, RenderApi.Direct3D9, "", "d3d9.dll", CpuArchitecture.X86),
            ("Dead Space.exe", true, RenderApi.DXGI, "", "dxgi.dll", CpuArchitecture.X64),
            ("Claire de Lune.exe", false, RenderApi.DXGI, "runtime\\bin", "dxgi.dll", CpuArchitecture.X64)
        ];
        foreach (var item in cases)
        {
            string directory = CreateTestDirectory("compatibility-deployment");
            var store = new StateStore(Path.Combine(directory, "central"));
            ManagerState state = CreateState(store.DirectoryPath);
            CacheCompatibilityFixture(state.Paths.Root);
            var registry = new InMemoryLayerRegistry();
            string recovery = Path.Combine(directory, "recovery");
            DeployRuntimePayload(state, recovery, registry);
            string root = Path.Combine(directory, "game");
            string folder = Path.Combine(root, item.Folder);
            Directory.CreateDirectory(folder);
            string executable = Path.Combine(root, item.Executable);
            File.WriteAllBytes(executable, CreateExecutableWithImports(item.Is64Bit,
                [item.Api == RenderApi.Direct3D9 ? "d3d9.dll" : "dxgi.dll"]));
            var game = new GameProfile { Executable = executable, Api = item.Api };
            game.Directory = ProfilePaths.DirectoryFor(state, game);
            InstallationPlan plan = DeploymentService.CreatePlan(state, game);
            AssertTrue(plan.GameDirectory == folder && plan.Links.Any(link =>
                link.Path == Path.Combine(folder, item.Proxy)));
            if (item.Folder.Length > 0)
            {
                AssertTrue(plan.Links.Count == 3 && plan.Describe(false).Contains(folder));
            }
            if (item.Is64Bit != (item.Architecture == CpuArchitecture.X64))
            {
                AssertTrue(plan.Describe(false).Contains("Runtime architecture: 64-bit"));
            }
            using (var transaction = new FileTransaction(recovery, "compatibility deployment"))
            {
                DeploymentService.Install(state, game, transaction, plan);
                state.Games.Add(game);
                store.Save(state, transaction);
                transaction.Commit();
            }
            AssertTrue(game.Architecture == item.Architecture && game.Links.All(DeploymentService.IsOwnedLink));
            if (item.Executable == "Dead Space.exe")
            {
                IniDocument config = IniDocument.Read(ConfigurationService.ConfigPath(game));
                AssertTrue((config.Get("DEPTH", "DepthCopyBeforeClears") == "1") ==
                    (item.Api == RenderApi.Direct3D9));
            }
            using var reopened = new Manager(new StateStore(store.DirectoryPath), registry);
            reopened.Open();
            GameProfile saved = reopened.State!.Games.Single();
            AssertTrue(saved.Architecture == item.Architecture && saved.Links.Count == plan.Links.Count);
            if (item.Folder.Length > 0)
            {
                string neighbor = Path.Combine(root, "other.exe");
                File.Copy(executable, neighbor);
                try
                {
                    await reopened.InstallAsync(new GameProfile { Executable = neighbor, Api = RenderApi.DXGI }, None);
                    throw new Exception("Profiles sharing the executable-side configuration must be rejected.");
                }
                catch (IOException error)
                {
                    AssertTrue(error.Message.Contains("Another managed profile"));
                }
                AssertTrue(game.Links.All(DeploymentService.IsOwnedLink));
            }
            await reopened.RepairAsync(null, None);
            AssertTrue(DeploymentService.GetInstallationWarning(reopened.State.Games.Single()) == null);
            await reopened.UnregisterAsync(saved.Id, None);
            AssertTrue(plan.Links.All(link => !FileTransaction.Exists(link.Path)) &&
                File.Exists(ConfigurationService.ConfigPath(saved)));
        }
    }

    private static Task CompatibilityAdoptionAndConflictsPreserveFilesAsync()
    {
        string directory = CreateTestDirectory("compatibility-adoption");
        ManagerState state = CreateState(Path.Combine(directory, "central"));
        CacheCompatibilityFixture(state.Paths.Root);
        string recovery = Path.Combine(directory, "recovery");
        DeployRuntimePayload(state, recovery, new InMemoryLayerRegistry());
        string root = Path.Combine(directory, "game");
        string bin = Path.Combine(root, "bin");
        Directory.CreateDirectory(bin);
        string executable = Path.Combine(root, "bms.exe");
        File.WriteAllBytes(executable, CreateExecutableWithImports(false, ["d3d9.dll"]));
        string rootConfig = Path.Combine(root, "ReShade.ini");
        string childConfig = Path.Combine(bin, "ReShade.ini");
        File.WriteAllText(rootConfig, "[INSTALL]\nBasePath=" + bin + "\n");
        File.WriteAllText(childConfig, "[GENERAL]\nPresetPath=Current.ini\nStartupPresetPath=Startup.ini\n" +
            "[DEPTH]\nDepthCopyBeforeClears=0\n");
        File.WriteAllText(Path.Combine(bin, "Current.ini"), "Techniques=CurrentEffect\n");
        File.WriteAllText(Path.Combine(bin, "Startup.ini"), "Techniques=StartupEffect\n");
        var game = new GameProfile { Executable = executable, Api = RenderApi.Direct3D9 };
        game.Directory = ProfilePaths.DirectoryFor(state, game);
        InstallationPlan plan = DeploymentService.CreatePlan(state, game);
        AssertTrue(plan.RequiresConfirmation && plan.ConfigurationSource == childConfig &&
            plan.PresetSource == Path.Combine(bin, "Current.ini"));
        File.WriteAllText(Path.Combine(bin, "d3d9.dll"), "Another mod owns this DLL");
        AssertTrue(DeploymentService.CreatePlan(state, game).Conflict!.Contains("unrecognized DLL"));
        File.Delete(Path.Combine(bin, "d3d9.dll"));
        File.AppendAllText(childConfig, "Custom=changed\n");
        AssertThrows<IOException>(() => DeploymentService.ValidatePlan(
            DeploymentService.CreatePlan(state, game), plan, true));
        plan = DeploymentService.CreatePlan(state, game);
        using (var transaction = new FileTransaction(recovery, "adopt compatibility installation"))
        {
            DeploymentService.Install(state, game, transaction, plan, replaceExisting: true);
            transaction.Commit();
        }
        IniDocument config = IniDocument.Read(ConfigurationService.ConfigPath(game));
        AssertTrue(config.Get("DEPTH", "DepthCopyBeforeClears") == "0" && config.Get("DEPTH", "Custom") == "changed");
        AssertTrue(IniDocument.Split(config.Get("GENERAL", "StartupPresetPath")).Single() ==
            Path.Combine(bin, "Startup.ini"));
        AssertTrue(File.ReadAllText(Path.Combine(game.Directory, "ReShadePreset.ini")) == "Techniques=CurrentEffect\n");
        AssertTrue(game.Links.Count == 3 && game.Links.All(DeploymentService.IsOwnedLink));

        string unsafeRoot = Path.Combine(directory, "unsafe");
        Directory.CreateDirectory(unsafeRoot);
        string unsafeExecutable = Path.Combine(unsafeRoot, "bms.exe");
        File.Copy(executable, unsafeExecutable);
        var unsafeGame = new GameProfile { Executable = unsafeExecutable, Api = RenderApi.Direct3D9 };
        AssertThrows<IOException>(() => DeploymentService.CreatePlan(state, unsafeGame));
        Directory.CreateSymbolicLink(Path.Combine(unsafeRoot, "bin"), bin);
        AssertThrows<IOException>(() => DeploymentService.CreatePlan(state, unsafeGame));

        const string bannedName = "BlockedFixture.exe";
        string banned = Path.Combine(directory, bannedName);
        File.WriteAllBytes(banned, CreateExecutableWithImports(true, ["dxgi.dll"]));
        var bannedGame = new GameProfile { Executable = banned, Api = RenderApi.DXGI };
        bannedGame.Directory = ProfilePaths.DirectoryFor(state, bannedGame);
        AssertTrue(DeploymentService.CreatePlan(state, bannedGame).Conflict!.Contains("blocking or banning"));
        return Task.CompletedTask;
    }

    private static async Task BootstrapAndRemixManagerDeploymentUsesResolvedExecutableAsync()
    {
        foreach (bool remix in new[] { false, true })
        {
            string directory = CreateTestDirectory("resolved-manager-deployment");
            var store = new StateStore(Path.Combine(directory, "central"));
            using var manager = new Manager(store, new InMemoryLayerRegistry());
            await manager.InitializeAsync(store.DirectoryPath, None);
            await manager.ImportRuntimeAsync(
                Path.Combine(repositoryRoot, ".validation", "ReShade_Setup_6.8.0.exe"),
                Path.Combine(repositoryRoot, ".validation", "ReShade_Setup_6.8.0_Addon.exe"), None);
            string root = Path.Combine(directory, "game");
            Directory.CreateDirectory(root);
            string selected = Path.Combine(root, "Launcher.exe");
            string resolved = remix ? Path.Combine(root, ".trex", "NvRemixBridge.exe") :
                Path.Combine(root, "Game", "Actual.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
            File.WriteAllBytes(selected, remix ? CreateExecutableWithImports(false, ["d3d9.dll"]) :
                BootstrapImage(Path.GetRelativePath(root, resolved)));
            File.WriteAllBytes(resolved, CreateExecutableWithImports(true, ["dxgi.dll"]));
            var request = new GameProfile { Executable = selected, Api = remix ? RenderApi.Vulkan : RenderApi.DXGI };
            InstallationPlan plan = DeploymentService.CreatePlan(manager.State!, request);
            AssertTrue(plan.Executable == resolved && plan.Describe(false).Contains(resolved));
            await manager.InstallAsync(request, None, plan);
            GameProfile installed = manager.State!.Games.Single();
            AssertTrue(installed.Executable == resolved && installed.Architecture == CpuArchitecture.X64 &&
                !File.Exists(Path.Combine(root, "ReShade.ini")));
            AssertTrue(installed.Links.All(DeploymentService.IsOwnedLink));
            await manager.UnregisterAsync(installed.Id, None);
        }
    }
}
