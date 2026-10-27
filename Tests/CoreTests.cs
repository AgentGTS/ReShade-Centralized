using System.Net;

namespace ReShadeCentralized.Tests;

internal static partial class TestSuite
{
    private static Task InstallationNoticeDistinguishesConfigAndConflictsAsync()
    {
        string directory = CreateTestDirectory("installation-notice");
        string executable = Path.Combine(directory, "game.exe");
        File.WriteAllBytes(executable, CreateExecutableWithImports(true, ["dxgi.dll"]));
        string shaders = Path.Combine(directory, "reshade-shaders");
        Directory.CreateDirectory(shaders);
        AssertTrue(DeploymentService.InspectExistingInstallation(executable, RenderApi.DXGI, []) == null);
        string config = Path.Combine(directory, "ReShade.ini");
        const string original = "[GENERAL]\nPerformanceMode=1\n";
        File.WriteAllText(config, original);
        ExistingInstallationNotice? notice = DeploymentService.InspectExistingInstallation(
            executable, RenderApi.Vulkan, []);
        AssertTrue(notice != null && !notice.BlocksInstallation);
        AssertTrue(notice!.Message.Contains("after confirmation") && notice.Message.Contains("reshade-shaders"));
        AssertTrue(File.ReadAllText(config) == original && Directory.Exists(shaders));
        AssertTrue(DeploymentService.InspectExistingInstallation(
            Path.Combine(directory, "missing.exe"), RenderApi.DXGI, []) == null);

        File.WriteAllText(Path.Combine(directory, "dxgi.dll"), "unrelated file");
        notice = DeploymentService.InspectExistingInstallation(executable, RenderApi.DXGI, []);
        AssertTrue(notice!.BlocksInstallation && notice.Message.Contains("unrecognized DLL"));
        notice = DeploymentService.InspectExistingInstallation(executable, RenderApi.OpenGL, []);
        AssertTrue(!notice!.BlocksInstallation, "An unrelated DLL for another API must not block this hook.");
        notice = DeploymentService.InspectExistingInstallation(executable, RenderApi.OpenGL,
            [new OwnedLink(config, Path.Combine(directory, "original-managed.ini"))]);
        AssertTrue(notice!.BlocksInstallation && notice.Message.Contains("changed outside this app"));
        return Task.CompletedTask;
    }

    private static Task DeploymentRejectsUnsupportedApisBeforeChangingFilesAsync()
    {
        string directory = CreateTestDirectory("unsupported-api");
        ManagerState state = CreateState(Path.Combine(directory, "central"));
        var game = new GameProfile
        {
            Api = (RenderApi)int.MaxValue,
            Directory = Path.Combine(state.Paths.Profiles, "unsupported")
        };
        using var transaction = new FileTransaction(Path.Combine(directory, "backups"), "unsupported API");

        AssertThrows<NotSupportedException>(() => DeploymentService.Install(state, game, transaction));
        AssertTrue(game.Links.Count == 0 && !Directory.Exists(game.Directory));
        return Task.CompletedTask;
    }

    private static Task IniPreservesCustomSettingsAndEscapedCommasAsync()
    {
        var document = new IniDocument("; hello\n" +
            "[general]\n" +
            "PresetPath=old\n" +
            "PresetPath=duplicate\n" +
            "PreprocessorDefinitions=DEPTH=1\n" +
            "[CUSTOM]\n" +
            "PresetPath=untouched\n" +
            "[GENERAL]\n" +
            "PresetPath=duplicate\n");
        document.Set("GENERAL", "PresetPath", IniDocument.Join([@"C:\a,b\preset.ini"]));
        AssertTrue(IniDocument.Split(document.Get("GENERAL", "PresetPath"))[0] == @"C:\a,b\preset.ini");
        AssertTrue(document.Get("CUSTOM", "PresetPath") == "untouched" &&
            document.Get("GENERAL", "PreprocessorDefinitions") == "DEPTH=1");
        AssertTrue(document.ToString().Contains("; hello") && !document.ToString().Contains("duplicate"));
        AssertTrue(!document.ToString().Contains("[general]"));
        document.Set("", "Apps", "one,two");
        AssertTrue(document.Get("", "Apps") == "one,two");
        return Task.CompletedTask;
    }

    private static Task FileTransactionRollsBackAndDiscardsRecoveryAsync()
    {
        string directory = CreateTestDirectory("transaction");
        string file = Path.Combine(directory, "one");
        string created = Path.Combine(directory, "created");
        string removed = Path.Combine(directory, "removed");
        File.WriteAllText(file, "original");
        File.WriteAllText(removed, "retain");
        string backup;
        using (var transaction = new FileTransaction(Path.Combine(directory, "backups"), "rollback"))
        {
            transaction.WriteText(file, "new");
            transaction.WriteText(created, "new");
            transaction.Delete(removed);
        }

        AssertTrue(File.ReadAllText(file) == "original" && !File.Exists(created) &&
            File.ReadAllText(removed) == "retain");
        using (var transaction = new FileTransaction(Path.Combine(directory, "backups"), "commit"))
        {
            backup = transaction.RecoveryDirectory;
            transaction.WriteText(file, "committed");
            transaction.Commit();
        }

        AssertTrue(!Directory.Exists(backup) && File.ReadAllText(file) == "committed");
        return Task.CompletedTask;
    }

    private static Task RollbackRejectsTamperedRecoveryBeforeChangingFilesAsync()
    {
        string directory = CreateTestDirectory("tamper");
        string file = Path.Combine(directory, "file");
        string backup;
        File.WriteAllText(file, "original");
        var transaction = new FileTransaction(Path.Combine(directory, "backups"), "tamper");
        backup = transaction.RecoveryDirectory;
        transaction.WriteText(file, "new");

        File.WriteAllText(Directory.GetFiles(backup, "*.bak").Single(), "tampered");
        AssertThrows<InvalidDataException>(() => FileTransaction.Rollback(backup, null));
        AssertTrue(File.ReadAllText(file) == "new");
        return Task.CompletedTask;
    }

    private static Task RollbackRejectsLockedDestinationsBeforeChangingFilesAsync()
    {
        string directory = CreateTestDirectory("restore-lock");
        string one = Path.Combine(directory, "one");
        string two = Path.Combine(directory, "two");
        string backup;
        File.WriteAllText(one, "old-one");
        File.WriteAllText(two, "old-two");
        using var transaction = new FileTransaction(Path.Combine(directory, "backups"), "pending");
        backup = transaction.RecoveryDirectory;
        transaction.WriteText(one, "new-one");
        transaction.WriteText(two, "new-two");

        using (var locked = new FileStream(one, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            AssertThrows<IOException>(() => FileTransaction.Rollback(backup, null));
            AssertTrue(File.ReadAllText(two) == "new-two");
        }

        FileTransaction.Rollback(backup, null);
        AssertTrue(File.ReadAllText(one) == "old-one" && File.ReadAllText(two) == "old-two");
        return Task.CompletedTask;
    }

    private static Task InterruptedPendingTransactionRecoversFilesAndRegistryAsync()
    {
        string directory = CreateTestDirectory("recover");
        string file = Path.Combine(directory, "file");
        File.WriteAllText(file, "old");
        var registry = new InMemoryLayerRegistry();
        var key = new LayerRegistration("HKCU", "32", "old.json");
        registry.Values[key] = 1;
        var transaction = new FileTransaction(Path.Combine(directory, "backups"), "interrupted", registry);
        transaction.WriteText(file, "new");
        transaction.SetRegistry(key, 0);
        FileTransaction.RecoverPending(Path.Combine(directory, "backups"), registry);
        AssertTrue(File.ReadAllText(file) == "old" && registry.Read(key) == 1);
        return Task.CompletedTask;
    }

    private static Task RegistryFailureRestoresEarlierWritesAsync()
    {
        string directory = CreateTestDirectory("registry-failure");
        var registry = new InMemoryLayerRegistry
        {
            FailWrite = 2
        };
        var a = new LayerRegistration("HKLM", "32", "a");
        var b = new LayerRegistration("HKLM", "64", "b");
        AssertThrows<IOException>(() =>
        {
            using var transaction = new FileTransaction(directory, "fail", registry);
            transaction.SetRegistry(a, 0);
            transaction.SetRegistry(b, 0);
            transaction.Commit();
        });
        AssertTrue(registry.Values.Count == 0);
        return Task.CompletedTask;
    }

    private static Task CrashRecoveryIgnoresIncompleteJournalEntriesAsync()
    {
        string directory = CreateTestDirectory("partial-event");
        string file = Path.Combine(directory, "file");
        File.WriteAllText(file, "old");
        var transaction = new FileTransaction(Path.Combine(directory, "backups"), "partial append");
        transaction.WriteText(file, "new");
        File.AppendAllText(Path.Combine(transaction.RecoveryDirectory, "events.jsonl"), "{\"File\":");
        FileTransaction.RecoverPending(Path.Combine(directory, "backups"), new InMemoryLayerRegistry());
        AssertTrue(File.ReadAllText(file) == "old");
        return Task.CompletedTask;
    }

    private static Task ArchiveRejectsUnsafePathsAndCaseCollisionsAsync()
    {
        foreach (string name in new[]
        {
            "../outside.fx",
            "/outside.fx",
            "C:/outside.fx",
            "shaders/a.fx:stream",
            "CON.fx",
            "shaders/dot./bad.fx"
        })
        {
            string directory = CreateTestDirectory("unsafe-zip");
            string zip = Path.Combine(directory, "package.zip");
            CreateArchive(zip, (name, "bad"));
            AssertThrows<InvalidDataException>(() => SafeArchive.Extract(zip, Path.Combine(directory, "out")));
        }

        string collision = CreateTestDirectory("collision");
        CreateArchive(Path.Combine(collision, "package.zip"), ("a.fx", "a"), ("A.fx", "b"));
        AssertThrows<InvalidDataException>(() => SafeArchive.Extract(
                Path.Combine(collision, "package.zip"),
                Path.Combine(collision, "out")));
        return Task.CompletedTask;
    }

    private static Task ArchiveValidatesAllEntriesBeforeExtractionAsync()
    {
        string directory = CreateTestDirectory("preflight");
        CreateArchive(Path.Combine(directory, "a.zip"), ("safe.fx", "yes"), ("../bad", "no"));
        AssertThrows<InvalidDataException>(() => SafeArchive.Extract(
                Path.Combine(directory, "a.zip"),
                Path.Combine(directory, "out")));
        AssertTrue(!File.Exists(Path.Combine(directory, "out", "safe.fx")));
        return Task.CompletedTask;
    }

    private static Task DownloadDiscoveryDistinguishesBuildsAndRejectsAmbiguityAsync()
    {
        string html = "<a href='/downloads/ReShade_Setup_6.8.0_Addon.exe'>addon</a><a " +
            "href=\"/downloads/ReShade_Setup_6.8.0.exe\">standard</a>";
        IReadOnlyDictionary<RuntimeBuild, string> links = RuntimeService.Discover(html);
        AssertTrue(!links[RuntimeBuild.Standard].Contains("Addon") && links[RuntimeBuild.FullAddon].Contains("Addon"));
        AssertThrows<InvalidDataException>(() => RuntimeService.Discover(html +
                "<a href='/downloads/ReShade_Setup_6.7.3.exe'>older</a>"));
        AssertThrows<InvalidDataException>(() => RuntimeService.Discover(
            "<a href='https://other.test/downloads/ReShade_Setup_6.8.0.exe'>bad</a>"));
        return Task.CompletedTask;
    }

    private static Task MalformedInstallerAndUnsupportedPeAreRejectedAsync()
    {
        string directory = CreateTestDirectory("malformed");
        string file = Path.Combine(directory, "bad.exe");
        File.WriteAllBytes(file, new byte[2048]);
        AssertThrows<InvalidDataException>(() => SafeArchive.ExtractInstaller(file, Path.Combine(directory, "out")));
        AssertThrows<InvalidDataException>(() => PortableExecutable.Architecture(file));
        return Task.CompletedTask;
    }

    private static async Task OfflineCatalogDeduplicatesPackagesAndResolvesDependenciesAsync()
    {
        string directory = CreateTestDirectory("catalog");
        using var downloads = new Downloads(new StubHttpMessageHandler(_ => new(HttpStatusCode.ServiceUnavailable)));
        var service = new ShaderService(downloads, new StateStore(directory));
        List<ShaderPackage> catalog = await service.CatalogAsync(null, None);
        AssertTrue(catalog.Count >= 5 && catalog.Count(package => package.Required) == 1);
        AssertTrue(catalog.Single(package => package.Required).Id ==
            ShaderService.PackageId("https://github.com/crosire/reshade-shaders/tree/slim"));
        AssertTrue(CompatibilityCatalog.Default.Count == 0);
        File.WriteAllText(Path.Combine(directory, ShaderService.CacheName), """
            [standard]
            RepositoryUrl=https://github.com/test/standard
            DownloadUrl=https://example.test/standard.zip
            Required=1
            [dependent]
            RepositoryUrl=https://github.com/yplebedev/BFBFX
            DownloadUrl=https://example.test/bfb.zip
            [framework]
            RepositoryUrl=https://github.com/zenteon/ZenteonFX
            DownloadUrl=https://example.test/framework.zip
            """);
        catalog = await service.CatalogAsync(null, None);
        AssertTrue(catalog.Select(package => ShaderService.CanonicalRepository(package.Repository)).Distinct()
            .Count() == catalog.Count);
        ShaderPackage bfb = catalog.Single(package => package.Repository.EndsWith("/BFBFX"));
        AssertTrue(ShaderService.WithDependencies([bfb], catalog)
            .Any(package => package.Repository.EndsWith("/ZenteonFX")));
        AssertTrue(ShaderService.PackageId("https://github.com/Radegast-FFXIV/reshade-shaders") ==
            ShaderService.PackageId("https://github.com/Radegast-FFXIV/Warp-FX"));
    }

    private static Task PackageInstallPreservesTreesAndExcludesExecutableAddonsAsync()
    {
        string directory = CreateTestDirectory("package");
        string extracted = Path.Combine(directory, "source");
        Directory.CreateDirectory(Path.Combine(extracted, "repo", "Shaders", "Includes"));
        Directory.CreateDirectory(Path.Combine(extracted, "repo", "Textures", "crt"));
        File.WriteAllText(Path.Combine(extracted, "repo", "Shaders", "main.fx"), "#include \"Includes/a.fxh\"");
        File.WriteAllText(Path.Combine(extracted, "repo", "Shaders", "Includes", "a.fxh"), "include");
        File.WriteAllText(Path.Combine(extracted, "repo", "Textures", "crt", "lut.png"), "texture");
        File.WriteAllText(Path.Combine(extracted, "repo", "Shaders", "evil.addon64"), "never");
        ManagerState state = CreateState(Path.Combine(directory, "central"));
        var package = new ShaderPackage
        {
            Id = "test",
            Name = "Test",
            Repository = "https://example.test/repo"
        };
        using (var transaction = new FileTransaction(Path.Combine(directory, "backups"), "package"))
        {
            ShaderService.InstallExtracted(package, extracted, state, transaction, None);
            transaction.Commit();
        }

        AssertTrue(File.Exists(Path.Combine(state.Paths.Shaders, "test", "Includes", "a.fxh")) &&
            File.Exists(Path.Combine(state.Paths.Textures, "test", "crt", "lut.png")));
        AssertTrue(!File.Exists(Path.Combine(state.Paths.Shaders, "test", "evil.addon64")) &&
            state.Packages.Single().Files.Count == 3);
        ShaderService.CheckUnmodified(state.Packages.Single(), state.Paths);
        File.WriteAllText(Path.Combine(state.Paths.Shaders, "test", "main.fx"), "local edit");
        AssertThrows<IOException>(() => ShaderService.CheckUnmodified(state.Packages.Single(), state.Paths));
        return Task.CompletedTask;
    }

    private static Task PackageUpdateRemovesOwnedFilesAndRollsBackCancellationAsync()
    {
        string directory = CreateTestDirectory("package-update");
        string extracted = Path.Combine(directory, "source");
        Directory.CreateDirectory(Path.Combine(extracted, "Shaders"));
        string shader = Path.Combine(extracted, "Shaders", "old.fx");
        File.WriteAllText(shader, "old");
        ManagerState state = CreateState(Path.Combine(directory, "central"));
        var package = new ShaderPackage
        {
            Id = "test",
            Name = "Test"
        };
        using (var transaction = new FileTransaction(Path.Combine(directory, "backups"), "v1"))
        {
            ShaderService.InstallExtracted(package, extracted, state, transaction, None);
            transaction.Commit();
        }

        File.Delete(shader);
        File.WriteAllText(Path.Combine(extracted, "Shaders", "new.fx"), "new");
        using (var transaction = new FileTransaction(Path.Combine(directory, "backups"), "v2"))
        {
            ShaderService.InstallExtracted(package, extracted, state, transaction, None);
            transaction.Commit();
        }

        AssertTrue(!File.Exists(Path.Combine(state.Paths.Shaders, "test", "old.fx")) &&
            File.Exists(Path.Combine(state.Paths.Shaders, "test", "new.fx")));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        AssertThrows<OperationCanceledException>(() =>
        {
            using var transaction = new FileTransaction(Path.Combine(directory, "backups"), "cancel");
            ShaderService.InstallExtracted(package, extracted, state, transaction, cancelled.Token);
        });
        AssertTrue(File.ReadAllText(Path.Combine(state.Paths.Shaders, "test", "new.fx")) == "new");
        return Task.CompletedTask;
    }

    private static Task ConfigurationRefreshPreservesCustomSettingsAndPresetsAsync()
    {
        string directory = CreateTestDirectory("config");
        string profile = Path.Combine(directory, "profile");
        Directory.CreateDirectory(profile);
        ManagerState state = CreateState(Path.Combine(directory, "central"));
        var game = new GameProfile
        {
            Directory = profile,
            Executable = Path.Combine(directory, "game.exe")
        };
        File.WriteAllText(
            Path.Combine(profile, "ReShade.ini"),
            "[GENERAL]\n" +
                "EffectSearchPaths=C:\\custom\\**\n" +
                "PresetPath=C:\\custom\\preset.ini\n" +
                "PreprocessorDefinitions=RESHADE_DEPTH_INPUT_IS_REVERSED=1\n" +
                "[DEPTH]\n" +
                "Copy=42\n" +
                "[OTHER]\n" +
                "SavePath=keep\n");
        using var transaction = new FileTransaction(Path.Combine(directory, "backups"), "config");
        ConfigurationService.Update(state, game, transaction);
        transaction.Commit();
        var document = IniDocument.Read(ConfigurationService.ConfigPath(game));
        AssertTrue(document.Get("DEPTH", "Copy") == "42" && document.Get("OTHER", "SavePath") == "keep");
        AssertTrue(document.Get("GENERAL", "PresetPath") == @"C:\custom\preset.ini" &&
            document.Get("GENERAL", "PreprocessorDefinitions")!.EndsWith("=1"));
        AssertTrue(IniDocument.Split(document.Get("GENERAL", "EffectSearchPaths")).Contains(@"C:\custom\**"));
        return Task.CompletedTask;
    }

    private static async Task ReleaseCheckHandlesMissingForkReleaseAsync()
    {
        string? requested = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            requested = request.RequestUri!.ToString();
            return new(HttpStatusCode.NotFound);
        });
        using var manager = new Manager(
            new StateStore(CreateTestDirectory("release")),
            new InMemoryLayerRegistry(),
            new Downloads(handler));
        AssertTrue(await manager.CheckReleaseAsync(None) == null &&
            requested!.Contains("AgentGTS/ReShade-Centralized"));
    }

    private static async Task ManagerCommittedOperationsLeaveNoHistoryAsync()
    {
        var store = new StateStore(CreateTestDirectory("manager-restore"));
        using var manager = new Manager(store, new InMemoryLayerRegistry());
        await manager.InitializeAsync(Path.Combine(store.DirectoryPath, "central"), None);
        await AssertThrowsAsync<InvalidOperationException>(() =>
            manager.InitializeAsync(Path.Combine(store.DirectoryPath, "other"), None));
        AssertTrue(!Directory.EnumerateFileSystemEntries(store.Recovery).Any());
        AssertTrue(!Directory.Exists(Path.Combine(store.DirectoryPath, "Backups")));
        AssertTrue(manager.State != null && File.Exists(store.StatePath));
    }
}
