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
                throw new InvalidOperationException("Choose a central folder first.");
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
                throw new InvalidOperationException("Already initialized.");
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
        Directory.CreateDirectory(path);
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
        return ChangeStateAsync("Install / repair " + request.Name, (updatedState, transaction) =>
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

            if (string.IsNullOrWhiteSpace(game.Name))
            {
                throw new ArgumentException("Game name is required.");
            }

            if (game.Executable != null)
            {
                game.Executable = Path.GetFullPath(game.Executable);
                if (updatedState.Games.Any(gameItem => gameItem.Id != game.Id && gameItem.Executable != null &&
                    FileTransaction.Same(
                        Path.GetDirectoryName(gameItem.Executable)!,
                        Path.GetDirectoryName(game.Executable)!)))
                {
                    throw new IOException(
                        "Another managed profile already uses this executable directory. Repair that profile instead.");
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

    public Task ForgetExternalVulkanAppAsync(string executable, CancellationToken cancellationToken)
    {
        return ChangeStateAsync("Remove external Vulkan consumer record", (updatedState, transaction) =>
        {
            updatedState.ExternalVulkanApps.RemoveAll(value => value.Equals(
                    executable,
                    StringComparison.OrdinalIgnoreCase));
            if (!updatedState.IgnoredVulkanApps.Contains(executable, StringComparer.OrdinalIgnoreCase))
            {
                updatedState.IgnoredVulkanApps.Add(executable);
            }

            vulkan.RemoveIfUnused(updatedState, transaction);
            if (updatedState.VulkanManaged)
            {
                vulkan.WriteApps(updatedState, transaction);
            }

            return Task.CompletedTask;
        }, cancellationToken);
    }

    public Task SetVulkanBuildAsync(RuntimeBuild build, CancellationToken cancellationToken)
    {
        return ChangeStateAsync("Switch global Vulkan build to " + build, (updatedState, transaction) =>
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
                    progress?.Report("Select an executable to relink legacy profile: " + game.Name);
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
                    "Select runtime executables for these legacy profiles before relocation: " +
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
        var messages = new List<string>();
        await RunSerializedAsync(async () =>
        {
            if (State == null)
            {
                throw new InvalidOperationException("Initialize first.");
            }

            IReadOnlyList<ShaderPackage> packages = ShaderService.WithDependencies(selected, catalog);
            foreach (ShaderPackage package in packages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string message = await UpdatePackageAsync(
                    package,
                    packages,
                    rememberSelection,
                    progress,
                    cancellationToken);
                messages.Add(message);
            }

            using var choices = new FileTransaction(Store.Backups, "Save shader package selection", registry);
            cancellationToken.ThrowIfCancellationRequested();
            ManagerState finalState = State.Clone();
            if (rememberSelection)
            {
                finalState.SelectedPackages = packages.Select(package => package.Id).ToList();
            }

            Store.Save(finalState, choices);
            choices.Commit();
            State = finalState;
            messages.AddRange(ShaderService.DuplicateEffects(State)
                .Select(value => "Duplicate effect filename: " +
                    value));
        }, cancellationToken);
        return messages;
    }

    private async Task<string> UpdatePackageAsync(
        ShaderPackage package,
        IReadOnlyList<ShaderPackage> selection,
        bool rememberSelection,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        string staging = CreateStagingDirectory();
        try
        {
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
            return "Updated: " + package.Name;
        }
        catch (Exception error) when (error is IOException or HttpRequestException or InvalidDataException
        or UnauthorizedAccessException)
        {
            return $"Preserved previous files / skipped {package.Name}: {error.Message}";
        }
        finally
        {
            DeleteStagingDirectory(staging);
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
