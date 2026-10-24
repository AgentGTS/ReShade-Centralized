using System.Text.Json;

namespace ReShadeCentralized.Tests;

internal static partial class TestSuite
{
    private static GameProfile CreateRegistrationGame(ManagerState state, string directory)
    {
        Directory.CreateDirectory(directory);
        string executable = Path.Combine(directory, "MyGame.exe");
        File.WriteAllBytes(executable, CreateExecutableWithImports(true, ["dxgi.dll"]));
        var game = new GameProfile { Executable = executable, Api = RenderApi.DXGI };
        game.Directory = ProfilePaths.DirectoryFor(state, game);
        return game;
    }

    private static Task InstallationReviewRequiresConsentAndRejectsChangesAsync()
    {
        string directory = CreateTestDirectory("review-consent");
        ManagerState state = CreateState(Path.Combine(directory, "central"));
        GameProfile game = CreateRegistrationGame(state, Path.Combine(directory, "game"));
        InstallationPlan clean = DeploymentService.CreatePlan(state, game);
        AssertTrue(!clean.RequiresConfirmation && clean.Conflict == null && clean.Links.Count == 2);
        AssertTrue(clean.Describe().Contains(game.Directory) && clean.Describe().Contains("dxgi.dll"));
        string summary = clean.Describe(includePaths: false);
        AssertTrue(summary.Contains("dxgi.dll") && summary.Contains("central runtime (ReShade64.dll)") &&
            summary.Contains("game profile (ReShade.ini)") && !summary.Contains(directory));
        string config = Path.Combine(clean.GameDirectory, "ReShade.ini");
        File.WriteAllText(config, "[GENERAL]\nPerformanceMode=1\n");
        InstallationPlan adoption = DeploymentService.CreatePlan(state, game);
        AssertTrue(adoption.RequiresConfirmation && adoption.Conflict == null);
        AssertThrows<IOException>(() => DeploymentService.ValidatePlan(adoption, adoption, false));
        AssertThrows<IOException>(() => DeploymentService.ValidatePlan(adoption, null, true));
        DeploymentService.ValidatePlan(adoption, adoption, true);
        File.AppendAllText(config, "PresetTransitionDuration=0\n");
        AssertThrows<IOException>(() => DeploymentService.ValidatePlan(
            DeploymentService.CreatePlan(state, game), adoption, true));
        File.WriteAllText(Path.Combine(clean.GameDirectory, "dxgi.dll"), "another injector");
        InstallationPlan conflict = DeploymentService.CreatePlan(state, game);
        AssertTrue(conflict.Conflict!.Contains(Path.Combine(clean.GameDirectory, "dxgi.dll")));
        string conflictSummary = conflict.Describe(includePaths: false);
        AssertTrue(conflictSummary.Contains("unrecognized DLL") && conflictSummary.Contains("dxgi.dll") &&
            !conflictSummary.Contains(directory));
        ExistingInstallationNotice? shortNotice = DeploymentService.InspectExistingInstallation(
            game.Executable, RenderApi.DXGI, [], includePaths: false);
        AssertTrue(shortNotice!.BlocksInstallation && shortNotice.Message.Contains("dxgi.dll") &&
            !shortNotice.Message.Contains(directory));
        var presetPlan = clean with
        {
            PresetSource = Path.Combine(directory, "OldPreset.ini"),
            LocalSearchPaths = [Path.Combine(clean.GameDirectory, "reshade-shaders", "Shaders", "**"),
                Path.Combine(clean.GameDirectory, "reshade-shaders", "Textures", "**")]
        };
        string presetSummary = presetPlan.Describe(includePaths: false);
        AssertTrue(presetSummary.Contains("OldPreset.ini") && presetSummary.Contains("ReShadePreset.ini") &&
            presetSummary.Contains(@"reshade-shaders\Shaders") &&
            presetSummary.Contains(@"reshade-shaders\Textures") && !presetSummary.Contains(directory));
        AssertThrows<IOException>(() => DeploymentService.ValidatePlan(conflict, conflict, true));
        return Task.CompletedTask;
    }

    private static Task AdoptionImportsPresetsAndLocalShadersAsync()
    {
        foreach (string selection in new[] { "relative", "absolute", "basepath", "missing", "empty" })
        {
            string directory = CreateTestDirectory("adopt-" + selection);
            ManagerState state = CreateState(Path.Combine(directory, "central"));
            string recovery = Path.Combine(directory, "recovery");
            DeployRuntimePayload(state, recovery);
            GameProfile game = CreateRegistrationGame(state, Path.Combine(directory, "game"));
            string gameDirectory = Path.GetDirectoryName(game.Executable)!;
            string shaders = Path.Combine(gameDirectory, "reshade-shaders", "Shaders");
            string textures = Path.Combine(gameDirectory, "reshade-shaders", "Textures");
            Directory.CreateDirectory(shaders);
            Directory.CreateDirectory(textures);
            File.WriteAllText(Path.Combine(shaders, "Custom.fx"), "local effect");
            File.WriteAllText(Path.Combine(textures, "Custom.png"), "local texture");
            string presetDirectory = selection == "basepath" ? Path.Combine(gameDirectory, "settings") : gameDirectory;
            Directory.CreateDirectory(presetDirectory);
            string source = Path.Combine(presetDirectory, "Selected,preset.ini");
            const string contents = "Techniques=LocalEffect\n[LocalEffect.fx]\nAmount=0.8\n";
            File.WriteAllText(source, contents);
            string selected = selection switch
            {
                "absolute" => source,
                "missing" => "Absent.ini",
                "empty" => "",
                _ => ".\\Selected,preset.ini"
            };
            string config = Path.Combine(gameDirectory, "ReShade.ini");
            File.WriteAllText(config, "[INSTALL]\nBasePath=" +
                (selection == "basepath" ? ".\\settings" : gameDirectory) +
                "\n[GENERAL]\nPresetPath=" + IniDocument.Join([selected]) +
                "\nEffectSearchPaths=" + shaders + "\n[DEPTH]\nCopy=42\n");
            InstallationPlan plan = DeploymentService.CreatePlan(state, game);
            using (var transaction = new FileTransaction(recovery, "adopt"))
            {
                DeploymentService.Install(state, game, transaction, reviewedPlan: plan, replaceExisting: true);
                transaction.Commit();
            }

            string imported = Path.Combine(game.Directory, "ReShadePreset.ini");
            var document = IniDocument.Read(ConfigurationService.ConfigPath(game));
            AssertTrue(IniDocument.Split(document.Get("GENERAL", "PresetPath")).Single() == imported);
            AssertTrue(File.ReadAllText(source) == contents && document.Get("DEPTH", "Copy") == "42");
            string expectedPreset = selection is "missing" or "empty" ? "Techniques=\n" : "Amount=0.8";
            AssertTrue(File.ReadAllText(imported).Contains(expectedPreset));
            string[] effects = IniDocument.Split(document.Get("GENERAL", "EffectSearchPaths"));
            AssertTrue(effects.Contains(Path.Combine(shaders, "**")) && !effects.Contains(shaders));
            AssertTrue(IniDocument.Split(document.Get("GENERAL", "TextureSearchPaths"))
                .Contains(Path.Combine(textures, "**")));
            File.WriteAllText(imported, "Techniques=UserEdits\n");
            state.Games.Add(game);
            using (var transaction = new FileTransaction(recovery, "repair"))
            {
                DeploymentService.Install(state, game, transaction);
                ConfigurationService.Update(state, game, transaction);
                transaction.Commit();
            }

            AssertTrue(File.ReadAllText(imported) == "Techniques=UserEdits\n");
            effects = IniDocument.Split(IniDocument.Read(ConfigurationService.ConfigPath(game))
                .Get("GENERAL", "EffectSearchPaths"));
            AssertTrue(effects.Count(path => path == Path.Combine(shaders, "**")) == 1);
            AssertTrue(!Directory.EnumerateFileSystemEntries(recovery).Any());
        }

        return Task.CompletedTask;
    }

    private static Task ReviewRejectsUnreadableAndChangedPresetsAsync()
    {
        string directory = CreateTestDirectory("preset-review");
        ManagerState state = CreateState(Path.Combine(directory, "central"));
        GameProfile game = CreateRegistrationGame(state, Path.Combine(directory, "game"));
        string config = Path.Combine(Path.GetDirectoryName(game.Executable)!, "ReShade.ini");
        string preset = Path.Combine(Path.GetDirectoryName(game.Executable)!, "preset.ini");
        File.WriteAllText(config, "[GENERAL]\nPresetPath=.\\preset.ini\n");
        File.WriteAllText(preset, "Techniques=Original\n");
        InstallationPlan plan = DeploymentService.CreatePlan(state, game);
        using (var locked = new FileStream(preset, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            AssertThrows<IOException>(() => DeploymentService.CreatePlan(state, game));
            AssertTrue(FileTransaction.Link(config) == null);
        }

        File.WriteAllText(preset, "Techniques=Changed\n");
        AssertThrows<IOException>(() => DeploymentService.ValidatePlan(
            DeploymentService.CreatePlan(state, game), plan, true));
        return Task.CompletedTask;
    }

    private static Task RecoveryCleanupFailureDoesNotUndoCommitAsync()
    {
        string directory = CreateTestDirectory("recovery-cleanup");
        string recovery = Path.Combine(directory, "recovery");
        string path = Path.Combine(directory, "file.ini");
        File.WriteAllText(path, "original");
        var warnings = new List<string>();
        using var transaction = new FileTransaction(recovery, "commit", reportWarning: warnings.Add);
        transaction.WriteText(path, "committed");
        using (var locked = new FileStream(Directory.GetFiles(transaction.RecoveryDirectory, "*.bak").Single(),
            FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            transaction.Commit();
            AssertTrue(warnings.Count == 1 && File.ReadAllText(path) == "committed");
        }

        FileTransaction.RecoverPending(recovery, new InMemoryLayerRegistry());
        AssertTrue(File.ReadAllText(path) == "committed" && !Directory.EnumerateFileSystemEntries(recovery).Any());
        return Task.CompletedTask;
    }

    private static async Task UnregisterRetainsNewExternalVulkanConsumersAsync()
    {
        string directory = CreateTestDirectory("unregister-shared-vulkan");
        var store = new StateStore(Path.Combine(directory, "manager"));
        var registry = new InMemoryLayerRegistry();
        using var manager = new Manager(store, registry);
        await manager.InitializeAsync(Path.Combine(directory, "central"), None);
        await manager.ImportRuntimeAsync(
            Path.Combine(repositoryRoot, ".validation", "ReShade_Setup_6.8.0.exe"),
            Path.Combine(repositoryRoot, ".validation", "ReShade_Setup_6.8.0_Addon.exe"), None);
        GameProfile game = CreateRegistrationGame(manager.State!, Path.Combine(directory, "game"));
        game.Api = RenderApi.Vulkan;
        await manager.InstallAsync(game, None);
        string apps = Path.Combine(manager.State!.VulkanLayerDirectory!, "ReShadeApps.ini");
        string external = Path.Combine(directory, "external.exe");
        File.WriteAllText(apps, "Apps=" + IniDocument.Join([game.Executable!, external]));
        LayerRegistration[] registrations = registry.Values.Keys.ToArray();
        await manager.UnregisterAsync(game.Id, None);
        AssertTrue(manager.State.Games.Count == 0 && manager.State.VulkanManaged);
        AssertTrue(IniDocument.Split(IniDocument.Read(apps).Get("", "Apps")).Single() == external);
        AssertTrue(registrations.All(registration => registry.Read(registration) == 0));
        AssertTrue(IniDocument.Split(IniDocument.Read(apps).Get("", "Apps")).Single() == external);
        AssertTrue(!FileTransaction.Exists(Path.Combine(Path.GetDirectoryName(game.Executable)!, "ReShade.ini")));

        // A second registration can be removed without deleting a layer another tool disabled.
        game = new GameProfile { Executable = game.Executable, Api = RenderApi.Vulkan };
        await manager.InstallAsync(game, None);
        File.WriteAllText(apps, "Apps=" + game.Executable);
        LayerRegistration changed = registry.Values.Keys.First();
        registry.Values[changed] = 1;
        UnregisterResult result = await manager.UnregisterAsync(game.Id, None);
        AssertTrue(result.PreservedFiles.Count == 0 && registry.Read(changed) == 1);
        AssertTrue(manager.State.Games.Count == 0 && manager.State.VulkanManaged);
    }

    private static async Task ManagerAdoptionEnforcesReviewAndPreservesChangedConfigAsync()
    {
        string directory = CreateTestDirectory("manager-adoption");
        var store = new StateStore(Path.Combine(directory, "manager"));
        using var manager = new Manager(store, new InMemoryLayerRegistry());
        await manager.InitializeAsync(Path.Combine(directory, "central"), None);
        await manager.ImportRuntimeAsync(
            Path.Combine(repositoryRoot, ".validation", "ReShade_Setup_6.8.0.exe"),
            Path.Combine(repositoryRoot, ".validation", "ReShade_Setup_6.8.0_Addon.exe"), None);
        GameProfile game = CreateRegistrationGame(manager.State!, Path.Combine(directory, "game"));
        string gameDirectory = Path.GetDirectoryName(game.Executable)!;
        string config = Path.Combine(gameDirectory, "ReShade.ini");
        string proxy = Path.Combine(gameDirectory, "dxgi.dll");
        string preset = Path.Combine(gameDirectory, "Active.ini");
        File.WriteAllText(preset, "Techniques=ExistingEffect\n");
        File.WriteAllText(config, "[GENERAL]\nPresetPath=.\\Active.ini\n");
        File.Copy(Path.Combine(manager.State!.Paths.StandardBinaries, "ReShade64.dll"), proxy);
        string originalProxy = FileTransaction.Hash(proxy);
        InstallationPlan plan = DeploymentService.CreatePlan(manager.State, game);
        await AssertThrowsAsync<IOException>(() => manager.InstallAsync(game, None, plan));
        File.AppendAllText(config, "PerformanceMode=1\n");
        await AssertThrowsAsync<IOException>(() => manager.InstallAsync(game, None, plan, replaceExisting: true));
        AssertTrue(manager.State.Games.Count == 0 && !Directory.Exists(game.Directory));
        AssertTrue(FileTransaction.Link(config) == null && FileTransaction.Hash(proxy) == originalProxy);
        plan = DeploymentService.CreatePlan(manager.State, game);
        await manager.InstallAsync(game, None, plan, replaceExisting: true);
        GameProfile installed = manager.State.Games.Single();
        AssertTrue(installed.Links.All(DeploymentService.IsOwnedLink));
        AssertTrue(File.ReadAllText(Path.Combine(installed.Directory, "ReShadePreset.ini")) ==
            "Techniques=ExistingEffect\n");
        await manager.InstallAsync(installed, None);
        File.Delete(config);
        File.WriteAllText(config, "another installer's configuration");
        UnregisterResult result = await manager.UnregisterAsync(game.Id, None);
        AssertTrue(result.PreservedFiles.Single() == config &&
            File.ReadAllText(config).StartsWith("another installer"));
        AssertTrue(!FileTransaction.Exists(proxy) && File.ReadAllText(preset) == "Techniques=ExistingEffect\n");
        AssertTrue(!Directory.EnumerateFileSystemEntries(store.Recovery).Any());
    }
}
