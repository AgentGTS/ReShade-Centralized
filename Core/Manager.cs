using System.Text.Json;

namespace ReShadeCentralized;

public sealed partial class Manager : IDisposable
{
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly Downloads downloads;
    private readonly ILayerRegistry registry;
    private readonly VulkanService vulkan;
    public StateStore Store { get; }

    public ManagerState? State { get; private set; }

    public ShaderService Shaders { get; }
    public event Action<string>? Warning;

    public Manager(StateStore store, ILayerRegistry registry, Downloads? downloads = null)
    {
        Store = store;
        this.registry = registry;
        vulkan = new(registry);
        this.downloads = downloads ?? new();
        Shaders = new(this.downloads, store);
    }

    public void Open()
    {
        Store.ReadSelection();
        if (!Store.IsSelected)
        {
            return;
        }

        FileTransaction.RecoverPending(Store.Recovery, registry, reportWarning: ReportWarning);
        State = LoadStorage();
        if (State == null && Store.PointerPath != null)
        {
            throw new InvalidDataException("Saved central storage has no registration state: " +
                Store.DirectoryPath + ". Select the existing central folder in Setup.");
        }
    }

    private void ReportWarning(string message) => Warning?.Invoke(message);

    private FileTransaction BeginTransaction(string description) =>
        new(Store.Recovery, description, registry, ReportWarning);

    private ManagerState? LoadStorage(CancellationToken cancellationToken = default)
    {
        ManagerState? state = Store.Load(out IReadOnlyList<string> repairs);
        if (state == null || repairs.Count == 0)
        {
            return state;
        }

        using FileTransaction transaction = BeginTransaction("Migrate / repair central storage settings");
        cancellationToken.ThrowIfCancellationRequested();
        Store.Save(state, transaction);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        foreach (string repair in repairs)
        {
            ReportWarning(repair);
        }
        return state;
    }

    public VulkanInspection InspectVulkan() => vulkan.Inspect(State);

    private async Task RunSerializedAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            await Task.Run(operation, cancellationToken);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task ChangeStateAsync(
        string description,
        Func<ManagerState, FileTransaction, Task> action,
        CancellationToken cancellationToken)
    {
        await RunSerializedAsync(async () =>
        {
            ManagerState updatedState = State?.Clone() ??
                throw new InvalidOperationException("Complete Setup and choose a central folder before " +
                    "using this action. Restart ReShade Centralized to open Setup.");
            using FileTransaction transaction = BeginTransaction(description);
            await action(updatedState, transaction);
            cancellationToken.ThrowIfCancellationRequested();
            Store.Save(updatedState, transaction);
            transaction.Commit();
            State = updatedState;
        }, cancellationToken);
    }

    public async Task InitializeAsync(string root, CancellationToken cancellationToken)
    {
        await RunSerializedAsync(() =>
        {
            if (State != null)
            {
                throw new InvalidOperationException("A central folder is already configured. " +
                    "Changing storage requires a fresh beta setup.");
            }

            string? previousDirectory = Store.IsSelected ? Store.DirectoryPath : null;
            try
            {
                Store.SelectDirectory(root);
                FileTransaction.RecoverPending(Store.Recovery, registry, reportWarning: ReportWarning);
                ManagerState? existing = LoadStorage(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (existing != null)
                {
                    State = existing;
                }
                else
                {
                    using FileTransaction transaction = BeginTransaction("Initialize central storage");
                    var updatedState = new ManagerState { Paths = CentralPaths.At(root) };
                    foreach (string path in updatedState.Paths.Directories())
                    {
                        FileTransaction.CheckParents(Path.Combine(path, "check"));
                        Directory.CreateDirectory(path);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    Store.Save(updatedState, transaction);
                    transaction.Commit();
                    State = updatedState;
                }
            }
            catch
            {
                Store.SelectDirectory(previousDirectory);
                throw;
            }

            try
            {
                Store.RememberSelection();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                ReportWarning("Central storage is ready, but its location could not be saved beside the app. " +
                    "Select the same central folder on the next launch. " + error.Message);
            }

            return Task.CompletedTask;
        }, cancellationToken);
    }

    private string CreateStagingDirectory()
    {
        string path = Path.Combine(Store.Staging, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (IOException error)
        {
            throw new IOException($"Cannot prepare the download folder {Store.Staging}. " +
                "Make sure this path is a writable folder rather than a file, then retry the download.", error);
        }

        return path;
    }

    private void DeleteStagingDirectory(string path)
    {
        if (!path.StartsWith(
            Path.GetFullPath(Store.Staging).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Refusing to clean a non-staging directory.");
        }

        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }

    public Task UpdateRuntimeAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        return ChangeStateAsync("Update official ReShade runtimes", async (updatedState, transaction) =>
        {
            string staging = CreateStagingDirectory();
            try
            {
                await new RuntimeService(downloads).StageAsync(staging, progress, cancellationToken);
                progress?.Report("Validating and deploying both runtime builds\u2026");
                RuntimeService.Deploy(staging, updatedState, transaction, cancellationToken);
                if (updatedState.VulkanManaged)
                {
                    vulkan.Enable(updatedState, transaction);
                }
            }
            finally
            {
                DeleteStagingDirectory(staging);
            }
        }, cancellationToken);
    }

    public Task ImportRuntimeAsync(
        string standardInstaller,
        string addonInstaller,
        CancellationToken cancellationToken)
    {
        return ChangeStateAsync("Import official ReShade installers", (updatedState, transaction) =>
        {
            string staging = CreateStagingDirectory();
            try
            {
                SafeArchive.ExtractInstaller(
                    standardInstaller,
                    Path.Combine(staging, RuntimeBuild.Standard.ToString()));
                SafeArchive.ExtractInstaller(
                    addonInstaller,
                    Path.Combine(staging, RuntimeBuild.FullAddon.ToString()));
                string standardVersion = RuntimeService.ValidatePayload(
                    Path.Combine(staging, RuntimeBuild.Standard.ToString()),
                    RuntimeBuild.Standard);
                string addonVersion = RuntimeService.ValidatePayload(
                    Path.Combine(staging, RuntimeBuild.FullAddon.ToString()),
                    RuntimeBuild.FullAddon);
                if (standardVersion != addonVersion)
                {
                    throw new InvalidDataException("The two official installers must have matching versions.");
                }

                RuntimeService.Deploy(staging, updatedState, transaction, cancellationToken);
                if (updatedState.VulkanManaged)
                {
                    vulkan.Enable(updatedState, transaction);
                }
            }
            finally
            {
                DeleteStagingDirectory(staging);
            }

            return Task.CompletedTask;
        }, cancellationToken);
    }

    public Task InstallAsync(
        GameProfile request,
        CancellationToken cancellationToken,
        InstallationPlan? reviewedPlan = null,
        bool replaceExisting = false)
    {
        string displayName = string.IsNullOrWhiteSpace(request.Executable) ? request.Name :
            Path.GetFileName(request.Executable);
        return ChangeStateAsync("Install / repair " + displayName, (updatedState, transaction) =>
        {
            GameProfile? existing = updatedState.Games.SingleOrDefault(gameItem => gameItem.Id == request.Id);
            GameProfile game = JsonSerializer.Deserialize<GameProfile>(
                JsonSerializer.Serialize(request, ManagerState.Json),
                ManagerState.Json)!;
            if (existing != null)
            {
                game.Links = existing.Links;
                game.Directory = existing.Directory;
            }
            else
            {
                game.Directory = "";
            }

            if (PortableExecutable.GetValidatedGamePath(game.Executable) == null)
            {
                throw new FileNotFoundException("Open Games > Install game or Repair / edit and select " +
                    "the game's actual local executable.");
            }

            game.Executable = Path.GetFullPath(game.Executable);
            game.Name = Path.GetFileName(game.Executable);
            if (updatedState.Games.Any(gameItem => gameItem.Id != game.Id &&
                FileTransaction.Same(Path.GetDirectoryName(gameItem.Executable)!,
                    Path.GetDirectoryName(game.Executable)!)))
            {
                throw new IOException("Another managed profile already uses this executable directory. " +
                    "Select that profile and choose Repair / edit instead.");
            }

            if (game.Api == RenderApi.Vulkan)
            {
                game.Build = updatedState.VulkanBuild;
            }

            game.Directory = ProfilePaths.DirectoryFor(updatedState, game);
            if (existing == null && (Directory.Exists(game.Directory) || FileTransaction.Exists(game.Directory)))
            {
                throw new IOException("Profile destination already exists: " + game.Directory);
            }

            InstallationPlan currentPlan = DeploymentService.CreatePlan(updatedState, game);
            DeploymentService.ValidatePlan(currentPlan, reviewedPlan, replaceExisting);
            updatedState.Games.RemoveAll(gameItem => gameItem.Id == game.Id);
            updatedState.Games.Add(game);
            DeploymentService.Install(updatedState, game, transaction, currentPlan, replaceExisting);
            if (game.Api == RenderApi.Vulkan)
            {
                vulkan.Enable(updatedState, transaction);
            }
            else if (updatedState.VulkanManaged)
            {
                vulkan.WriteApps(updatedState, transaction, enroll: false);
            }

            return Task.CompletedTask;
        }, cancellationToken);
    }

    public async Task<UnregisterResult> UnregisterAsync(string id, CancellationToken cancellationToken)
    {
        UnregisterResult? result = null;
        await ChangeStateAsync("Unregister game", (updatedState, transaction) =>
        {
            GameProfile game = updatedState.Games.Single(gameItem => gameItem.Id == id);
            result = DeploymentService.Remove(game, transaction);
            updatedState.Games.Remove(game);
            if (updatedState.VulkanManaged)
            {
                vulkan.WriteApps(updatedState, transaction, enroll: false);
            }

            return Task.CompletedTask;
        }, cancellationToken);
        return result!;
    }

    public Task SetVulkanBuildAsync(RuntimeBuild build, CancellationToken cancellationToken)
    {
        return ChangeStateAsync("Switch global Vulkan build to " + DisplayNames.For(build), (updatedState, transaction) =>
        {
            updatedState.VulkanBuild = build;
            foreach (GameProfile? game in updatedState.Games.Where(game => game.Api == RenderApi.Vulkan))
            {
                game.Build = build;
            }

            vulkan.Enable(updatedState, transaction);
            return Task.CompletedTask;
        }, cancellationToken);
    }

    public Task RepairAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        return ChangeStateAsync("Repair managed links and configuration", (updatedState, transaction) =>
        {
            foreach (GameProfile game in updatedState.Games)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report("Repairing " + game.Name);
                DeploymentService.Install(updatedState, game, transaction);
            }

            if (updatedState.VulkanManaged)
            {
                vulkan.Enable(updatedState, transaction);
            }

            return Task.CompletedTask;
        }, cancellationToken);
    }

    public async Task<List<string>> UpdatePackagesAsync(
        IReadOnlyList<ShaderPackage> selected,
        IReadOnlyList<ShaderPackage> catalog,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        bool rememberSelection = true)
    {
        PackageUpdateReport report = await UpdatePackagesWithReportAsync(
            selected, catalog, progress, cancellationToken, rememberSelection);
        return report.Messages;
    }

    public Task<PackageUpdateReport> UpdatePackagesWithReportAsync(
        IReadOnlyList<ShaderPackage> selected,
        IReadOnlyList<ShaderPackage> catalog,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        bool rememberSelection = true) =>
        RunPackageBatchAsync(selected, catalog, progress, cancellationToken, rememberSelection);

    private async Task<PackageUpdateReport> RunPackageBatchAsync(
        IReadOnlyList<ShaderPackage> selected,
        IReadOnlyList<ShaderPackage> catalog,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        bool rememberSelection,
        ShaderSelectionPlan? reviewedPlan = null)
    {
        var report = new PackageUpdateReport();
        void Report(string message)
        {
            report.Messages.Add(message);
            progress?.Report(message);
        }

        await RunSerializedAsync(async () =>
        {
            if (State == null)
            {
                throw new InvalidOperationException("Complete Setup and choose a central folder before " +
                    "installing shader packages. Restart ReShade Centralized to open Setup.");
            }

            ShaderSelectionPlan? currentPlan = reviewedPlan == null ? null : CreateShaderSelectionPlan(selected, catalog);
            if (reviewedPlan != null && (currentPlan!.Fingerprint != reviewedPlan.Fingerprint ||
                !currentPlan.Removals.Select(package => package.Id)
                    .SequenceEqual(reviewedPlan.Removals.Select(package => package.Id), StringComparer.OrdinalIgnoreCase)))
            {
                throw new IOException("Shader selections or installed packages changed. Review the removals again.");
            }

            IReadOnlyList<ShaderPackage> packages = ShaderService.WithDependencies(
                selected, catalog, (package, message) =>
                {
                    report.FailedCount++;
                    Report($"Skipped {package.Name}: {message}");
                });
            ShaderPackage[] desired = selected.Concat(packages).DistinctBy(package => package.Id).ToArray();
            if (reviewedPlan != null)
            {
                SaveShaderChoices(desired, cancellationToken);
            }
            foreach (ShaderPackage package in packages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ShaderPackage[] missingDependencies = ShaderService.Dependencies(package, packages)
                    .Where(dependency => !IsPackageAvailable(dependency.Id)).ToArray();
                if (missingDependencies.Length > 0)
                {
                    report.FailedCount++;
                    Report($"Skipped {package.Name}: required packages are unavailable: " +
                        string.Join(", ", missingDependencies.Select(dependency => dependency.Name)) +
                        ". In Shader packages, select those packages and retry Update selected. " +
                        "Existing files were retained.");
                    continue;
                }

                (bool installed, string message) = await UpdatePackageAsync(
                    package,
                    desired,
                    rememberSelection,
                    progress,
                    report.Messages,
                    cancellationToken);
                if (installed)
                {
                    report.InstalledCount++;
                }
                else
                {
                    report.FailedCount++;
                }

                Report(message);
            }

            if (reviewedPlan != null)
            {
                foreach (InstalledPackage removal in OrderShaderRemovals(currentPlan!.Removals, catalog))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    (bool removed, string message) = RemoveShaderPackage(removal, desired, catalog, cancellationToken);
                    if (removed)
                    {
                        report.RemovedCount++;
                    }
                    else
                    {
                        report.FailedCount++;
                    }
                    Report(message);
                }
            }

            using FileTransaction choices = BeginTransaction("Save shader package selection");
            cancellationToken.ThrowIfCancellationRequested();
            ManagerState finalState = State.Clone();
            if (rememberSelection)
            {
                finalState.SelectedPackages = selected.Concat(catalog.Where(package => package.Required))
                    .Concat(packages).Select(package => package.Id).Distinct().ToList();
            }

            Store.Save(finalState, choices);
            choices.Commit();
            State = finalState;
            foreach (string duplicate in ShaderService.DuplicateEffects(State))
            {
                Report("Duplicate effect filename: " + duplicate);
            }
        }, cancellationToken);
        return report;
    }

    public Task PurgeShaderFolderAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        return RunSerializedAsync(() =>
        {
            ManagerState updatedState = State?.Clone() ??
                throw new InvalidOperationException("Choose central storage in Setup before purging shaders.");
            string root = updatedState.Paths.ShaderRoot;
            FileTransaction.CheckParents(Path.Combine(root, "check"));
            if (FileTransaction.Link(root) != null || File.Exists(root))
            {
                throw new IOException("The central shader folder must be a regular directory: " + root);
            }

            var files = new List<string>();
            if (Directory.Exists(root))
            {
                foreach (string file in ShaderService.EnumerateFiles(root))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    files.Add(file);
                }
            }

            progress?.Report($"Purging {files.Count} files from {root}");
            using (FileTransaction transaction = BeginTransaction("Purge central shader folder"))
            {
                foreach (string file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    transaction.Delete(file);
                }

                updatedState.Packages.Clear();
                cancellationToken.ThrowIfCancellationRequested();
                Store.Save(updatedState, transaction);
                transaction.Commit();
                State = updatedState;
            }

            try
            {
                RemoveEmptyShaderFolders(root);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                ReportWarning("Shader files were purged, but empty folders could not be removed: " + root +
                    ". " + error.Message);
            }

            progress?.Report("Central shader folder purged. Choose Update selected to reinstall packages.");
            return Task.CompletedTask;
        }, cancellationToken);
    }

    private static void RemoveEmptyShaderFolders(string directory)
    {
        FileTransaction.CheckParents(Path.Combine(directory, "check"));
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (string child in Directory.EnumerateDirectories(directory))
        {
            RemoveEmptyShaderFolders(child);
        }

        // Nonrecursive deletion keeps any files added since the purge was committed.
        Directory.Delete(directory, recursive: false);
    }

    private bool IsPackageAvailable(string id)
    {
        InstalledPackage? installed = State!.Packages.SingleOrDefault(package => package.Id == id);
        if (installed == null || installed.Files.Count == 0)
        {
            return false;
        }

        foreach (string file in installed.Files.Keys)
        {
            string[] parts = file.Split('/', 2);
            string root = parts[0] == "shaders" ? State.Paths.Shaders : State.Paths.Textures;
            string directory = ShaderService.PackageDirectory(root, id);
            if (parts.Length != 2 || !File.Exists(FileTransaction.Contained(directory, parts[1])))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<(bool Installed, string Message)> UpdatePackageAsync(
        ShaderPackage package,
        IReadOnlyList<ShaderPackage> selection,
        bool rememberSelection,
        IProgress<string>? progress,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        string? staging = null;
        try
        {
            staging = CreateStagingDirectory();
            ManagerState updatedState = State!.Clone();
            InstalledPackage? installed = updatedState.Packages.SingleOrDefault(item => item.Id == package.Id);
            if (installed != null)
            {
                ShaderService.CheckUnmodified(installed, updatedState.Paths);
            }

            progress?.Report("Updating " + package.Name);
            string source = await Shaders.StageAsync(package, staging, progress, cancellationToken);
            using FileTransaction transaction = BeginTransaction("Update shader package: " + package.Name);
            ShaderService.InstallExtracted(package, source, updatedState, transaction, cancellationToken);
            foreach (GameProfile game in updatedState.Games)
            {
                ConfigurationService.Update(updatedState, game, transaction);
            }

            if (rememberSelection)
            {
                updatedState.SelectedPackages = selection.Select(item => item.Id).ToList();
            }

            cancellationToken.ThrowIfCancellationRequested();
            Store.Save(updatedState, transaction);
            transaction.Commit();
            State = updatedState;
            return (true, "Installed: " + package.Name);
        }
        catch (Exception error) when (error is IOException or HttpRequestException or InvalidDataException
        or UnauthorizedAccessException || (error is OperationCanceledException &&
            !cancellationToken.IsCancellationRequested))
        {
            string explanation = error is InvalidDataException ?
                "The package archive is invalid or unsupported. Open Shader packages > Refresh catalog, " +
                "then retry. Details: " + error.Message : UserErrors.Describe(error);
            return (false, $"Failed {package.Name}: {explanation}\n" +
                "Existing files were retained. Retry this package in Shader packages > Update selected. " +
                $"Repository: {package.Repository}");
        }
        finally
        {
            if (staging != null)
            {
                try
                {
                    DeleteStagingDirectory(staging);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    string warning = $"Could not remove temporary files for {package.Name}: {staging}. " +
                        "After this operation finishes, close programs using this folder and remove it " +
                        "to reclaim disk space. " + error.Message;
                    messages.Add(warning);
                    progress?.Report(warning);
                }
            }
        }
    }

    public Task<string?> CheckReleaseAsync(CancellationToken cancellationToken) =>
        CheckReleaseAsync(includePrereleases: false, cancellationToken);

    public async Task<string?> CheckReleaseAsync(bool includePrereleases, CancellationToken cancellationToken)
    {
        try
        {
            string? latestTag = null;
            ReleaseVersion? latestVersion = null;
            for (var page = 1; ; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var json = JsonDocument.Parse(await downloads.TextAsync(
                        "https://api.github.com/repos/AgentGTS/ReShade-Centralized/releases" +
                        $"?per_page=100&page={page}",
                        cancellationToken));
                foreach (JsonElement release in json.RootElement.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string? tag = release.GetProperty("tag_name").GetString();
                    if (release.GetProperty("draft").GetBoolean() ||
                        !ReleaseVersion.TryParse(tag, out ReleaseVersion? version))
                    {
                        continue;
                    }

                    bool isPrerelease = release.GetProperty("prerelease").GetBoolean() || version.IsPrerelease;
                    if (!includePrereleases && isPrerelease)
                    {
                        continue;
                    }

                    if (latestVersion == null || version.CompareTo(latestVersion) > 0)
                    {
                        latestVersion = version;
                        latestTag = tag;
                    }
                }

                if (json.RootElement.GetArrayLength() < 100)
                {
                    return latestTag;
                }
            }
        }
        catch (HttpRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public void Dispose()
    {
        downloads.Dispose();
        operationGate.Dispose();
    }
}
