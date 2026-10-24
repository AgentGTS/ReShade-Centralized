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
        await RunTestAsync("Explicit Vulkan removal preserves other installers' entries and runtime files",
            VulkanRemovalPreservesOtherRegistrationsAndFilesAsync);
        await RunTestAsync("Last Vulkan unregister keeps shared support and standalone consumers",
            LastVulkanUnregisterPreservesStandaloneConsumersAsync);
        await RunTestAsync("Vulkan removal preserves disabled, changed, linked and unowned registrations",
            VulkanRemovalPreservesChangedAndUnownedEntriesAsync);
        await RunTestAsync("Vulkan removal rolls back failures and recovers interrupted registry changes",
            VulkanRemovalRollsBackAndRecoversRegistryChangesAsync);
        await RunTestAsync("Shader selection installs, updates, removes custom package files and rechecks review",
            ShaderSelectionInstallsUpdatesAndRemovesAsync);
        await RunTestAsync("Failed removals retain required dependencies and unchecked intent across restart",
            ShaderRemovalFailureRetainsDependenciesAndIntentAsync);
        await RunTestAsync("Canceled removals preserve committed operations and saved selection intent",
            ShaderRemovalCancellationPreservesCompletedChangesAsync);
        await RunTestAsync("Selection migration protects installed packages and add-on paths use one directory",
            ShaderSelectionMigrationAndAddonPathsAsync);
        await RunTestAsync("Shader package removal refuses linked and unsafe folders",
            ShaderRemovalRejectsLinkedFoldersAsync);
        await RunTestAsync(
            "Central format upgrades repair metadata while preserving profiles and unrelated files",
            CentralStateMigrationRepairsRecoverableSettingsAsync);
        await RunTestAsync(
            "Central storage recovers profile identity, version markers and editable JSON syntax",
            CentralStateMigrationRecoversProfileIdentityAsync);
        await RunTestAsync(
            "Unrecoverable, ambiguous and future settings remain untouched",
            CentralStateMigrationPreservesUnrecoverableDataAsync);
        await RunTestAsync(
            "Storage migration rolls back failed saves and recovers interrupted operations",
            CentralStateMigrationSaveFailureAndRecoveryAsync);
        await RunTestAsync(
            "Existing ReShade symlinks retain protection for unrelated hooks and other APIs",
            SymlinkAdoptionProtectsUnrelatedHooksAsync);
        await RunTestAsync(
            "Default storage is offered with reset guidance and missing folders are recreated",
            DefaultStorageOffersFreshSetupGuidanceAsync);
        await RunTestAsync(
            "Central storage reopens after deleting the app and recovers interrupted changes",
            CentralStorageSurvivesApplicationDeletionAsync);
        await RunTestAsync(
            "Storage selection rejects unsafe or invalid folders without changing state or the pointer",
            StorageSelectionRejectsInvalidFoldersAsync);
        await RunTestAsync(
            "Unavailable storage and pointer failures allow recovery through setup",
            StoragePointerFailuresRemainRecoverableAsync);
        await RunTestAsync(
            "Fresh beta setup ignores old assets and uses one storage layout",
            FreshBetaSetupIgnoresOldAssetsAsync);
        await RunTestAsync(
            "Incompatible beta formats require fresh setup without rewriting state",
            IncompatibleBetaStateRequiresFreshSetupAsync);
        await RunTestAsync(
            "Current state recovers missing link records and rejects unknown executable paths",
            CurrentBetaStateRequiresCompleteProfilesAsync);
        await RunTestAsync(
            "Installation health finds deleted, retargeted and identical-file replacements without changing files",
            InstallationHealthDetectsMissingAndReplacedLinksAsync);
        await RunTestAsync(
            "Installation health checks hook links and flags incomplete profiles",
            InstallationHealthChecksHooksAndUnconfiguredProfilesAsync);
        await RunTestAsync(
            "Installation review requires consent and rejects changed files",
            InstallationReviewRequiresConsentAndRejectsChangesAsync);
        await RunTestAsync(
            "Preset review rejects unreadable or changed selections",
            ReviewRejectsUnreadableAndChangedPresetsAsync);
        await RunTestAsync(
            "Recovery cleanup failures preserve committed changes",
            RecoveryCleanupFailureDoesNotUndoCommitAsync);
        await RunTestAsync(
            "Graphics API detection reads x86/x64 normal and delay imports and leaves ambiguous APIs unresolved",
            GraphicsApiDetectionFindsOnlyUnambiguousImportsAsync);
        await RunTestAsync(
            "Graphics API detection fails open for missing, locked and malformed files and honors cancellation",
            GraphicsApiDetectionFailsOpenAndHonorsCancellationAsync);
        await RunTestAsync(
            "Graphics API detection rejects invalid directory bounds, unterminated tables and unsupported machines",
            GraphicsApiDetectionRejectsInvalidDirectoriesAndMachinesAsync);
        await RunTestAsync(
            "Graphics API detection handles read-only executables without edits or retained file handles",
            GraphicsApiDetectionLeavesFilesUnchangedAndReleasesHandlesAsync);
        await RunTestAsync(
            "Game paths reject unsafe locations, malformed files and DLLs before inspection",
            GamePathValidationRejectsUnsafeAndNonExecutablePathsAsync);
        await RunTestAsync(
            "Game paths reject executable links and linked parent directories",
            GamePathValidationRejectsLinksAsync);
        await RunTestAsync(
            "INI preserves sections, unknown settings, comments, and escaped commas",
            IniPreservesCustomSettingsAndEscapedCommasAsync);
        await RunTestAsync(
            "File transaction rolls back failures and discards committed recovery data",
            FileTransactionRollsBackAndDiscardsRecoveryAsync);
        await RunTestAsync(
            "Recovery tampering is rejected before any destination changes",
            RollbackRejectsTamperedRecoveryBeforeChangingFilesAsync);
        await RunTestAsync(
            "Rollback preflights locked destinations before reverting other files",
            RollbackRejectsLockedDestinationsBeforeChangingFilesAsync);
        await RunTestAsync(
            "Interrupted pending transaction recovers files and registry",
            InterruptedPendingTransactionRecoversFilesAndRegistryAsync);
        await RunTestAsync(
            "Registry failure restores earlier writes",
            RegistryFailureRestoresEarlierWritesAsync);
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
            "Installation notices distinguish unmanaged configuration, DLL conflicts, and externally changed links",
            InstallationNoticeDistinguishesConfigAndConflictsAsync);
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
            "Failed downloads, timeouts, and corrupt archives do not block healthy shader packages",
            PackageFailuresDoNotBlockHealthyDownloadsAsync);
        await RunTestAsync(
            "Shader folder purge resolves unmanaged package conflicts and preserves game data and choices",
            ShaderPurgeResolvesUnmanagedPackageConflictsAsync);
        await RunTestAsync(
            "Shader purge failures roll back files and inventory; cancellation leaves them intact",
            ShaderPurgeRollsBackFailuresAndHonorsCancellationAsync);
        await RunTestAsync(
            "Shader purge refuses linked folders and files without changing external data",
            ShaderPurgeRejectsLinkedDirectoriesAndFilesAsync);
        await RunTestAsync(
            "Failed package updates preserve previous files and local edits while other packages install",
            FailedPackageUpdatesPreserveExistingFilesAndLocalEditsAsync);
        await RunTestAsync(
            "Missing and failed dependencies skip only affected packages; retained dependencies remain usable",
            FailedDependenciesSkipOnlyAffectedPackagesAsync);
        await RunTestAsync(
            "Canceling a shader batch keeps completed installs and rolls back the active package",
            PackageCancellationKeepsCompletedInstallsAsync);
        await RunTestAsync(
            "Cleanup locks and staging failures are isolated from other package installations",
            PackageCleanupAndStagingFailuresAreIsolatedAsync);
        await RunTestAsync(
            "Out-of-order actions identify the missing setup step and the control used to fix it",
            OutOfOrderActionsExplainRecoveryAsync);
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
            "Manager commits operations without keeping backup history",
            ManagerCommittedOperationsLeaveNoHistoryAsync);
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
            "Existing and dangling ReShade symlinks support review, preset adoption and rollback",
            ExistingReShadeSymlinksCanBeAdoptedAsync);
        await RunTestAsync(
            "Retargeted managed ReShade links require review rather than blocking installation",
            ReplacedManagedReShadeLinksRequireReviewAsync);
        await RunTestAsync(
            "Games, Vulkan layers and package state survive an app purge and fresh installation",
            RegisteredGamesSurviveApplicationDeletionAsync);
        await RunTestAsync(
            "Existing shared layers preserve registry values, runtimes, comments and foreign app entries",
            ExistingVulkanLayersRetainRegistrationsAndForeignEntriesAsync);
        await RunTestAsync(
            "App-owned layers persist across repair, build switch, updates and last-game unregister",
            OwnedVulkanLayerSurvivesRepairUpdatesAndLastGameAsync);
        await RunTestAsync(
            "Adoption copies selected presets and retains local shaders through repair",
            AdoptionImportsPresetsAndLocalShadersAsync);
        await RunTestAsync(
            "Unregister retains external Vulkan consumers and changed registrations",
            UnregisterRetainsNewExternalVulkanConsumersAsync);
        await RunTestAsync(
            "Manager adoption enforces review and preserves changed configuration",
            ManagerAdoptionEnforcesReviewAndPreservesChangedConfigAsync);
        await RunTestAsync(
            "Vulkan inspection checks both views, runtime identity and external consumers without writing",
            VulkanInspectionVerifiesRegistrationWithoutChangesAsync);
        await RunTestAsync(
            "Vulkan inventory refreshes after install, restart, build switch, removal and registration changes",
            VulkanInventoryFollowsGameAndRegistrationChangesAsync);
        await RunTestAsync(
            "Executable filenames supply game names; changing executables preserves profile IDs and presets",
            ExecutableChangesKeepProfilesAndPresetsAsync);
        await RunTestAsync(
            "Unmanaged ReShade adoption requires review and unregister preserves user files",
            UnmanagedInstallationReviewAndUnregisterPreserveUserFilesAsync);
        await RunTestAsync(
            "Build identity and Vulkan manifest traversal validation",
            BuildIdentityAndVulkanManifestTraversalValidationAsync);
        await RunTestAsync(
            "Vulkan reuses both existing registry views without migrating external installations",
            VulkanMigratesBothViewsAndExternalApplicationsAsync);
        await RunTestAsync(
            "Vulkan remains registered even when no consumers are recorded",
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
            "Both game architectures and build switches target the correct stable central DLL",
            GameLinksTargetCorrectArchitecturesAndBuildsAsync);
    }
}
