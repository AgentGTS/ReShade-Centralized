using System.IO.Compression;
using Microsoft.Win32;

namespace ReShadeCentralized.Tests;

internal static partial class TestSuite
{
    private static string testRoot = "";
    private static string repositoryRoot = "";
    private static string runtimePayload = "";
    private static int passedCount;
    private static int failedCount;
    private static int skippedCount;
    private static readonly CancellationToken None = CancellationToken.None;

    private static string CreateTestDirectory(string name)
    {
        string path = Path.Combine(testRoot, name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void AssertTrue(bool condition, string message = "Assertion failed")
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }

    private static void AssertThrows<T>(Action action)
    where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new Exception("Expected " + typeof(T).Name);
    }

    private static async Task RunTestAsync(string name, Func<Task> test)
    {
        try
        {
            await test();
            Console.WriteLine("PASS " + name);
            passedCount++;
        }
        catch (Exception error)
        {
            Console.WriteLine("FAIL " + name + "\n" + error);
            failedCount++;
        }
    }

    private static Task RunSynchronousTest(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    private static void CreateArchive(string path, params (string Name, string Content)[] entries)
    {
        using ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach ((string Name, string Content) item in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(item.Name).Open());
            writer.Write(item.Content);
        }
    }

    private static ManagerState CreateState(string path) => new()
    {
        Paths = CentralPaths.At(path)
    };

    public static async Task<int> Main(string[] args)
    {
        repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
        testRoot = Path.Combine(repositoryRoot, ".validation", "tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        Console.WriteLine("Isolated test workspace: " + testRoot);
        await RunTestAsync(
            "INI preserves sections, unknown settings, comments, and escaped commas",
            IniPreservesCustomSettingsAndEscapedCommasAsync);
        await RunTestAsync(
            "File transaction rolls back overwrite, creation, deletion; committed backup restores",
            FileTransactionRollsBackAndRestoresCommittedBackupAsync);
        await RunTestAsync(
            "Backup tampering is rejected before any destination changes",
            RestoreRejectsTamperedBackupsBeforeChangingFilesAsync);
        await RunTestAsync(
            "Restore preflights locked destinations before reverting other files",
            RestoreRejectsLockedDestinationsBeforeChangingFilesAsync);
        await RunTestAsync(
            "Interrupted pending transaction recovers files and registry",
            InterruptedPendingTransactionRecoversFilesAndRegistryAsync);
        await RunTestAsync("Registry failure restores earlier writes", RegistryFailureRestoresEarlierWritesAsync);
        await RunTestAsync(
            "Crash recovery ignores an unfinished final journal append",
            CrashRecoveryIgnoresIncompleteJournalEntriesAsync);
        await RunTestAsync(
            "Archive traversal, absolute paths, streams, reserved names, and case collisions rejected",
            ArchiveRejectsUnsafePathsAndCaseCollisionsAsync);
        await RunTestAsync(
            "Archive validates all entries before writing a valid preceding file",
            ArchiveValidatesAllEntriesBeforeExtractionAsync);
        await RunTestAsync(
            "Official download discovery separates standard and add-on, rejects ambiguity",
            DownloadDiscoveryDistinguishesBuildsAndRejectsAmbiguityAsync);
        await RunTestAsync(
            "Malformed installer and unsupported PE are rejected",
            MalformedInstallerAndUnsupportedPeAreRejectedAsync);
        await RunTestAsync(
            "Unsupported rendering APIs are rejected before deployment changes files",
            DeploymentRejectsUnsupportedApisBeforeChangingFilesAsync);
        await RunTestAsync(
            "Offline catalog falls back, extras deduplicate, BFBFX resolves Zenteon",
            OfflineCatalogDeduplicatesPackagesAndResolvesDependenciesAsync);
        await RunTestAsync(
            "Package layout preserves nested includes/textures and never installs executable add-ons",
            PackageInstallPreservesTreesAndExcludesExecutableAddonsAsync);
        await RunTestAsync(
            "Package updates remove only obsolete owned files; cancellation rolls back",
            PackageUpdateRemovesOwnedFilesAndRollsBackCancellationAsync);
        await RunTestAsync(
            "Legacy import retains custom paths, profiles, presets and marks executable unknown",
            LegacyImportPreservesProfilesAndCustomPathsAsync);
        await RunTestAsync(
            "Configuration refresh retains depth, custom settings, custom search paths and preset",
            ConfigurationRefreshPreservesCustomSettingsAndPresetsAsync);
        await RunTestAsync(
            "Release absence is normal and uses the fork endpoint",
            ReleaseCheckHandlesMissingForkReleaseAsync);
        await RunTestAsync(
            "Release versions order numeric prereleases, stable versions, and build metadata correctly",
            ReleaseVersionsFollowSemanticPrecedenceAsync);
        await RunTestAsync(
            "Release checks filter channels and drafts, ignore invalid tags, and choose the highest version",
            ReleaseCheckRespectsChannelsAndVersionOrderAsync);
        await RunTestAsync(
            "Release checks paginate and preserve HTTP failures and cancellation",
            ReleaseCheckPaginatesAndPreservesFailuresAsync);
        await RunTestAsync(
            "Relocation remaps a custom active preset and include paths within managed storage",
            RelocationRemapsPresetAndIncludePathsAsync);
        await RunTestAsync(
            "Manager restore undoes newer transactions before restoring the chosen state",
            ManagerRestoreRevertsNewerTransactionsFirstAsync);
        string installers = Path.Combine(repositoryRoot, ".validation");
        if (File.Exists(Path.Combine(installers, "ReShade_Setup_6.8.0.exe")) &&
            File.Exists(Path.Combine(installers, "ReShade_Setup_6.8.0_Addon.exe")))
        {
            await RunTestAsync(
                "Official 6.8.0 installers extract both architectures and Vulkan manifests",
                () => OfficialInstallersExtractBothArchitecturesAndVulkanAsync(installers));
            if (runtimePayload.Length > 0 && Directory.Exists(Path.Combine(runtimePayload, "Standard")))
            {
                await RunIntegrationTestsAsync();
            }
        }
        else
        {
            Console.WriteLine("SKIP official runtime integration (put both official 6.8.0 installers in .validation)");
            skippedCount++;
        }

        if (args.Contains("--windows-registry") && OperatingSystem.IsWindows())
        {
            await RunTestAsync(
                "Windows registry DWORD values in both views, isolated HKCU keys only",
                () => RunSynchronousTest(VerifyWindowsRegistry));
        }

        if (args.Contains("--online") || args.Contains("--online-mlut"))
        {
            await RunOnlineTestsAsync(args.Contains("--online-mlut"));
        }

        Console.WriteLine($"Results: {passedCount} passed, {failedCount} failed, {skippedCount} skipped.");
        return failedCount == 0 ? 0 : 1;
    }

    private static void VerifyWindowsRegistry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string testKey = @"SOFTWARE\ReShadeCentralizedTests\" + Guid.NewGuid().ToString("N");
        var backend = new WindowsLayerRegistry(testKey);
        LayerRegistration[] keys = new[]
        {
            new LayerRegistration("HKCU", "32", Path.Combine(testRoot, "test32.json")),
            new LayerRegistration("HKCU", "64", Path.Combine(testRoot, "test64.json"))
        };
        try
        {
            using (var transaction = new FileTransaction(
                CreateTestDirectory("real-registry"),
                "isolated HKCU registry",
                backend))
            {
                foreach (LayerRegistration? key in keys)
                {
                    transaction.SetRegistry(key, 0);
                }

                foreach (LayerRegistration? key in keys)
                {
                    AssertTrue(backend.Read(key) == 0);
                    using var view = RegistryKey.OpenBaseKey(
                        RegistryHive.CurrentUser,
                        key.View == "32" ? RegistryView.Registry32 : RegistryView.Registry64);
                    using RegistryKey? actual = view.OpenSubKey(testKey + @"\ImplicitLayers");
                    AssertTrue(actual!.GetValueKind(key.Manifest) == RegistryValueKind.DWord);
                }
            }

            foreach (LayerRegistration? key in keys)
            {
                AssertTrue(backend.Read(key) == null);
            }
        }
        finally
        {
            foreach (RegistryView view in new[]
            {
                RegistryView.Registry32,
                RegistryView.Registry64
            })
            {
                using var key = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
                key.DeleteSubKeyTree(testKey, false);
            }
        }
    }

    private static async Task RunOnlineTestsAsync(bool mlutOnly)
    {
        using var downloads = new Downloads();
        await RunTestAsync(
            "Online official runtime discovery, download, and payload validation",
            () => OnlineRuntimeDownloadValidatesOfficialPayloadsAsync(downloads));
        List<ShaderPackage> catalog = await new ShaderService(
            downloads,
            new StateStore(CreateTestDirectory("online-catalog"))).CatalogAsync(
            null,
            None);
        string[] repositories =
        [
            "crosire/reshade-shaders/tree/slim",
            "martymcmodding/iMMERSE",
            "luluco250/FXShaders",
            "Matsilagi/RSRetroArch",
            "EndlesslyFlowering/ReShade_HDR_shaders",
            "Radegast-FFXIV/Warp-FX",
            "Heathen/Pirate-Shaders",
            "TheGordinho/MLUT",
            "ConstantineRudenko/Shaders",
            "rj200/Glamarye_Fast_Effects_for_ReShade"
        ];
        IEnumerable<string> selectedRepositories = mlutOnly ? ["TheGordinho/MLUT"] : repositories;
        foreach (string name in selectedRepositories)
        {
            await RunTestAsync(
                "Current shader archive layout: " + name,
                () => ShaderArchivePreservesTheCurrentPackageLayoutAsync(downloads, catalog, name));
        }
    }

    private static void DeployRuntimePayload(ManagerState state, string backups, InMemoryLayerRegistry? registry = null)
    {
        using var transaction = new FileTransaction(backups, "runtime", registry);
        RuntimeService.Deploy(runtimePayload, state, transaction, None);
        transaction.Commit();
    }

    private static async Task RunIntegrationTestsAsync()
    {
        await RunTestAsync(
            "Build identity and Vulkan manifest traversal validation",
            BuildIdentityAndVulkanManifestTraversalValidationAsync);
        await RunTestAsync(
            "Vulkan migrates both views and external applications, switches build and rolls back",
            VulkanMigratesBothViewsAndExternalApplicationsAsync);
        await RunTestAsync(
            "Vulkan remains while consumers exist, removes only owned values when unused",
            VulkanRemovalRetainsSharedLayerAndUnownedFilesAsync);
        await RunTestAsync(
            "Vulkan repair preserves newly competing installer consumers and handles explicit layers",
            VulkanRepairPreservesNewlyCompetingInstallerConsumersAsync);
        await RunTestAsync(
            "Locked runtime update preserves the previous complete runtime",
            LockedRuntimeUpdatePreservesPreviousInstallationAsync);
        await RunTestAsync(
            "Real symlinks: install, repair, API change, removal and ownership protection",
            GameLinksSupportRepairApiChangesAndRemovalAsync);
        await RunTestAsync(
            "Manager relocation repairs existing game links and restore returns them",
            ManagerRelocationRepairsLinksAndPreservesOriginalDataAsync);
        await RunTestAsync(
            "Both game architectures and build switches target the correct stable central DLL",
            GameLinksTargetCorrectArchitecturesAndBuildsAsync);
    }
}
