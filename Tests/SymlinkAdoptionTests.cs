namespace ReShadeCentralized.Tests;

internal static partial class TestSuite
{
    private static Task ExistingReShadeSymlinksCanBeAdoptedAsync()
    {
        foreach (bool broken in new[] { false, true })
        {
            string directory = CreateTestDirectory("adopt-reshade-links");
            ManagerState state = CreateState(Path.Combine(directory, "central"));
            string recovery = Path.Combine(directory, "recovery");
            DeployRuntimePayload(state, recovery);
            GameProfile game = CreateRegistrationGame(state, Path.Combine(directory, "game"));
            string local = Path.GetDirectoryName(game.Executable)!;
            string previous = Path.Combine(directory, "previous-install");
            Directory.CreateDirectory(previous);
            string runtime = Path.Combine(previous, "ReShade64.dll");
            string config = Path.Combine(previous, "ReShade.ini");
            string preset = Path.Combine(previous, "Selected.ini");
            if (!broken)
            {
                File.Copy(Path.Combine(state.Paths.StandardBinaries, "ReShade64.dll"), runtime);
                File.WriteAllText(preset, "Original selected preset");
                File.WriteAllText(config, "[INSTALL]\nBasePath=" + previous +
                    "\n[GENERAL]\nPresetPath=.\\Selected.ini\n[Custom]\nKeep=42\n");
            }

            string hookLink = Path.Combine(local, "dxgi.dll");
            string configLink = Path.Combine(local, "ReShade.ini");
            File.CreateSymbolicLink(hookLink, runtime);
            File.CreateSymbolicLink(configLink, config);
            string? runtimeHash = broken ? null : FileTransaction.Hash(runtime);
            string? originalConfig = broken ? null : File.ReadAllText(config);
            InstallationPlan plan = DeploymentService.CreatePlan(state, game);
            AssertTrue(plan.Conflict == null && plan.RequiresConfirmation);
            AssertTrue(plan.Describe().Contains("Replace link:") && !plan.Describe().Contains("Game folder:"));
            AssertTrue(plan.Describe().Contains(runtime) &&
                plan.Describe().Contains("Files that existing links point to are kept"));
            AssertThrows<IOException>(() => DeploymentService.ValidatePlan(plan, plan, false));
            if (!broken)
            {
                File.AppendAllText(config, "ChangedAfterReview=1\n");
                AssertThrows<IOException>(() => DeploymentService.ValidatePlan(
                    DeploymentService.CreatePlan(state, game), plan, true));
                File.WriteAllText(config, originalConfig);
            }

            // A failed operation must restore the previous links, including dangling targets.
            using (var rollback = new FileTransaction(recovery, "Adoption rollback"))
            {
                DeploymentService.Install(state, game, rollback, plan, replaceExisting: true);
            }
            AssertTrue(FileTransaction.Link(hookLink) == runtime && FileTransaction.Link(configLink) == config);
            game.Links.Clear();
            plan = DeploymentService.CreatePlan(state, game);
            using (var transaction = new FileTransaction(recovery, "Adopt existing ReShade links"))
            {
                DeploymentService.Install(state, game, transaction, plan, replaceExisting: true);
                transaction.Commit();
            }

            AssertTrue(game.Links.All(DeploymentService.IsOwnedLink));
            string selected = Path.Combine(game.Directory, "ReShadePreset.ini");
            if (broken)
            {
                AssertTrue(!File.Exists(runtime) && !File.Exists(config));
            }
            else
            {
                AssertTrue(FileTransaction.Hash(runtime) == runtimeHash && File.ReadAllText(config) == originalConfig);
                AssertTrue(File.ReadAllText(preset) == "Original selected preset" &&
                    File.ReadAllText(selected) == "Original selected preset");
                AssertTrue(IniDocument.Read(ConfigurationService.ConfigPath(game)).Get("Custom", "Keep") == "42");
            }

            File.WriteAllText(selected, "Managed preset after adoption");
            using var repair = new FileTransaction(recovery, "Repair adopted ReShade");
            DeploymentService.Install(state, game, repair);
            repair.Commit();
            AssertTrue(File.ReadAllText(selected) == "Managed preset after adoption");
        }

        return Task.CompletedTask;
    }

    private static Task SymlinkAdoptionProtectsUnrelatedHooksAsync()
    {
        string directory = CreateTestDirectory("symlink-conflicts");
        ManagerState state = CreateState(Path.Combine(directory, "central"));
        GameProfile game = CreateRegistrationGame(state, Path.Combine(directory, "game"));
        string local = Path.GetDirectoryName(game.Executable)!;
        string hook = Path.Combine(local, "dxgi.dll");
        string unrelated = Path.Combine(directory, "ReShade64.dll");
        File.WriteAllText(unrelated, "Unrelated DLL despite its filename");
        File.CreateSymbolicLink(hook, unrelated);
        AssertTrue(DeploymentService.CreatePlan(state, game).Conflict != null);
        File.Delete(hook);
        File.CreateSymbolicLink(hook, Path.Combine(directory, "missing-other-tool.dll"));
        AssertTrue(DeploymentService.CreatePlan(state, game).Conflict != null);
        File.Delete(hook);
        File.CreateSymbolicLink(Path.Combine(local, "d3d9.dll"), Path.Combine(directory, "missing", "ReShade64.dll"));
        AssertTrue(DeploymentService.CreatePlan(state, game).Conflict != null);
        AssertTrue(File.ReadAllText(unrelated) == "Unrelated DLL despite its filename");
        return Task.CompletedTask;
    }

    private static async Task ReplacedManagedReShadeLinksRequireReviewAsync()
    {
        string directory = CreateTestDirectory("retargeted-managed-links");
        var store = new StateStore(Path.Combine(directory, "central"));
        using var manager = new Manager(store, new InMemoryLayerRegistry());
        await manager.InitializeAsync(manager.Store.DirectoryPath, None);
        await manager.ImportRuntimeAsync(Path.Combine(repositoryRoot, ".validation", "ReShade_Setup_6.8.0.exe"),
            Path.Combine(repositoryRoot, ".validation", "ReShade_Setup_6.8.0_Addon.exe"), None);
        GameProfile game = CreateRegistrationGame(manager.State!, Path.Combine(directory, "game"));
        await manager.InstallAsync(game, None);
        GameProfile managed = manager.State!.Games.Single();
        string hook = Path.Combine(Path.GetDirectoryName(game.Executable)!, "dxgi.dll");
        string otherRuntime = Path.Combine(manager.State.Paths.AddonBinaries, "ReShade64.dll");
        File.Delete(hook);
        File.CreateSymbolicLink(hook, otherRuntime);
        InstallationPlan plan = DeploymentService.CreatePlan(manager.State, managed);
        AssertTrue(plan.Conflict == null && plan.RequiresConfirmation);
        await AssertThrowsAsync<IOException>(() => manager.RepairAsync(null, None));
        AssertTrue(FileTransaction.Link(hook) == otherRuntime);
        await manager.InstallAsync(managed, None, plan, replaceExisting: true);
        AssertTrue(manager.State.Games.Single().Links.All(DeploymentService.IsOwnedLink));
    }
}
