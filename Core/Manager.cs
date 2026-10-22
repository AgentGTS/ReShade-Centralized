using System.Text.Json;

namespace ReShadeCentralized;

public sealed class Manager : IDisposable
{
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly Downloads downloads;
    private readonly ILayerRegistry registry;
    private readonly VulkanService vulkan;
    public StateStore Store { get; }

    public ManagerState? State { get; private set; }

    public ShaderService Shaders { get; }

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
        FileTransaction.RecoverPending(Store.Backups, registry);
        State = Store.Load();
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
            using var transaction = new FileTransaction(Store.Backups, description, registry);
            await action(updatedState, transaction);
            cancellationToken.ThrowIfCancellationRequested();
            Store.Save(updatedState, transaction);
            transaction.Commit();
            State = updatedState;
        }, cancellationToken);
    }

    public async Task InitializeAsync(string root, string? legacyIni, CancellationToken cancellationToken)
    {
        await RunSerializedAsync(() =>
        {
            if (State != null)
            {
                throw new InvalidOperationException("A central folder is already configured. To move it, " +
                    "use Runtime & storage > Relocate central folder.");
            }

            using var transaction = new FileTransaction(Store.Backups, "Initialize / import legacy settings", registry);
            ManagerState updatedState = MigrationService.Create(root, legacyIni, transaction);
            string template = Path.Combine(
                legacyIni == null ? Store.DirectoryPath : Path.GetDirectoryName(legacyIni)!,
                "ReShade_Template.ini");
            if (File.Exists(template))
            {
                transaction.Capture(template);
                updatedState.ConfigurationTemplate = File.ReadAllText(template);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Store.Save(updatedState, transaction);
            transaction.Commit();
            State = updatedState;
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
                progress?.Report("Validating and deploying both runtime builds …");
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

    public Task InstallAsync(GameProfile request, CancellationToken cancellationToken)
    {
        string displayName = string.IsNullOrWhiteSpace(request.Executable) ? request.Name :
            Path.GetFileName(request.Executable);
        return ChangeStateAsync("Install / repair " + displayName, (updatedState, transaction) =>
        {
            ManagerState previous = updatedState.Clone();
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
                game.Directory = FileTransaction.Contained(updatedState.Paths.Profiles, game.Id);
            }

            if (string.IsNullOrWhiteSpace(game.Executable))
            {
                game.Executable = null;
            }

            if (game.Executable != null)
            {
                game.Executable = Path.GetFullPath(game.Executable);
                game.Name = Path.GetFileName(game.Executable);
                if (updatedState.Games.Any(gameItem => gameItem.Id != game.Id && gameItem.Executable != null &&
                    FileTransaction.Same(
                        Path.GetDirectoryName(gameItem.Executable)!,
                        Path.GetDirectoryName(game.Executable)!)))
                {
                    throw new IOException(
                        "Another managed profile already uses this executable directory. Open Games, select " +
                        "that profile, and choose Repair / edit instead of installing a second profile.");
                }
            }

            if (game.Api == RenderApi.Vulkan)
            {
                game.Build = updatedState.VulkanBuild;
            }

            updatedState.Games.RemoveAll(gameItem => gameItem.Id == game.Id);
            updatedState.Games.Add(game);
            DeploymentService.Install(updatedState, game, transaction, previous);
            if (game.Api == RenderApi.Vulkan)
            {
                updatedState.ExternalVulkanApps.RemoveAll(value => game.Executable != null &&
                    FileTransaction.Same(value, game.Executable));
                vulkan.Enable(updatedState, transaction);
            }
            else if (updatedState.VulkanManaged)
            {
                vulkan.RemoveIfUnused(updatedState, transaction);
                if (updatedState.VulkanManaged)
                {
                    vulkan.Enable(updatedState, transaction);
                }
            }

            return Task.CompletedTask;
        }, cancellationToken);
    }

    public Task RemoveAsync(string id, CancellationToken cancellationToken)
    {
        return ChangeStateAsync("Remove managed game links", (updatedState, transaction) =>
        {
            GameProfile game = updatedState.Games.Single(gameItem => gameItem.Id == id);
            DeploymentService.Remove(game, transaction);
            updatedState.Games.Remove(game);
            if (updatedState.VulkanManaged)
            {
                vulkan.RemoveIfUnused(updatedState, transaction);
                if (updatedState.VulkanManaged)
                {
                    vulkan.Enable(updatedState, transaction);
                }
            }

            return Task.CompletedTask;
        }, cancellationToken);
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
                if (game.Executable == null)
                {
                    progress?.Report($"Skipped {game.Name}: open Games > Repair / edit and select its Executable, " +
                        "then retry Repair all.");
                    continue;
                }

                DeploymentService.Install(updatedState, game, transaction);
            }

            if (updatedState.VulkanManaged)
            {
                vulkan.Enable(updatedState, transaction);
            }

            return Task.CompletedTask;
        }, cancellationToken);
    }

    public Task RelocateAsync(string root, CancellationToken cancellationToken)
    {
        return ChangeStateAsync("Relocate central storage", (updatedState, transaction) =>
        {
            string[] unknown = updatedState.Games.Where(game => game.Executable == null)
                .Select(game => game.Name)
                .ToArray();
            if (unknown.Length > 0)
            {
                throw new InvalidOperationException(
                    "Open Games > Repair / edit and select the Executable for these legacy profiles " +
                    "before relocating: " +
                    string.Join(", ", unknown));
            }

            MigrationService.Relocate(updatedState, root, transaction, cancellationToken);
            updatedState.RuntimeHashes = updatedState.RuntimeHashes.ToDictionary(
                item => RemapRuntimeHash(State!.Paths, updatedState.Paths, item.Key),
                item => item.Value,
                StringComparer.OrdinalIgnoreCase);
            if (updatedState.VulkanManaged)
            {
                vulkan.Enable(updatedState, transaction);
            }

            return Task.CompletedTask;
        }, cancellationToken);
    }

    private static string RemapRuntimeHash(CentralPaths previousState, CentralPaths updatedState, string path)
    {
        foreach (RuntimeBuild build in Enum.GetValues<RuntimeBuild>())
        {
            if (path.StartsWith(previousState.Binaries(build).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(
                    updatedState.Binaries(build),
                    Path.GetRelativePath(previousState.Binaries(build), path));
            }
        }

        return path;
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

    public async Task<PackageUpdateReport> UpdatePackagesWithReportAsync(
        IReadOnlyList<ShaderPackage> selected,
        IReadOnlyList<ShaderPackage> catalog,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        bool rememberSelection = true)
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

            IReadOnlyList<ShaderPackage> packages = ShaderService.WithDependencies(
                selected, catalog, (package, message) =>
                {
                    report.FailedCount++;
                    Report($"Skipped {package.Name}: {message}");
                });
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
                        ". In Shader packages, select those packages and retry Install / update selected. " +
                        "Existing files were retained.");
                    continue;
                }

                (bool installed, string message) = await UpdatePackageAsync(
                    package,
                    packages,
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

            using var choices = new FileTransaction(Store.Backups, "Save shader package selection", registry);
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
            ManagerState previousState = State!.Clone();
            ManagerState updatedState = State.Clone();
            InstalledPackage? installed = updatedState.Packages.SingleOrDefault(item => item.Id == package.Id);
            if (installed != null)
            {
                ShaderService.CheckUnmodified(installed, updatedState.Paths);
            }

            progress?.Report("Updating " + package.Name);
            string source = await Shaders.StageAsync(package, staging, progress, cancellationToken);
            using var transaction = new FileTransaction(
                Store.Backups,
                "Update shader package: " + package.Name,
                registry);
            ShaderService.InstallExtracted(package, source, updatedState, transaction, cancellationToken);
            foreach (GameProfile game in updatedState.Games)
            {
                ConfigurationService.Update(updatedState, game, transaction, previousState);
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
                "Existing files were retained. Retry this package in Shader packages > Install / update selected. " +
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

    public async Task RestoreToAsync(string backupDirectory, CancellationToken cancellationToken)
    {
        await RunSerializedAsync(() =>
        {
            string selected = Path.GetFullPath(backupDirectory);
            if (!Directory.GetDirectories(Store.Backups).Any(value => FileTransaction.Same(value, selected)))
            {
                throw new IOException("Select a manager backup.");
            }

            foreach (string path in Directory.GetDirectories(Store.Backups).OrderDescending())
            {
                if (string.CompareOrdinal(Path.GetFileName(path), Path.GetFileName(selected)) < 0)
                {
                    break;
                }

                if (FileTransaction.ReadJournal(path).Status != "restored")
                {
                    FileTransaction.Restore(path, registry);
                }
            }

            State = Store.Load();
            return Task.CompletedTask;
        }, cancellationToken);
    }

    public void Dispose()
    {
        downloads.Dispose();
        operationGate.Dispose();
    }
}
