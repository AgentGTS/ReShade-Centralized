using System.Diagnostics;

namespace ReShadeCentralized;

public sealed partial class MainForm
{
    private readonly StorageLoadingIndicator storageLoading = new();
    private bool storageControlsLocked;

    private void SetStorageControlsLocked(bool locked)
    {
        storageControlsLocked = locked;
        contentRoot.Enabled = !locked;
    }

    private void PositionStorageLoading()
    {
        int header = gamesGrid.ColumnHeadersVisible ? gamesGrid.ColumnHeadersHeight : 0;
        storageLoading.Bounds = new Rectangle(
            0, header, gamesGrid.ClientSize.Width,
            Math.Max(0, gamesGrid.ClientSize.Height - header));
    }

    private sealed record GameDisplayRow(
        string Id,
        string? InstallationWarning,
        string ShaderWarning,
        string Name,
        string API,
        string Build,
        string Architecture,
        string Executable);

    private sealed record ViewPresentation(
        ManagerState? State,
        List<GameDisplayRow> Games,
        Dictionary<RuntimeBuild, string> RuntimeVersions,
        VulkanInspection Vulkan,
        Dictionary<string, bool> PackageCompleteness);

    private ViewPresentation? presentation;
    private bool storageLoadFailed;
    private CancellationTokenSource? gameWarningRefreshCancellation;

    private static List<GameDisplayRow> PrepareGames(
        ManagerState? state,
        CancellationToken cancellationToken,
        IProgress<StorageLoadProgress>? progress,
        IReadOnlyList<ShaderPackage> packages)
    {
        var reportWatch = Stopwatch.StartNew();
        int gameCount = state?.Games.Count ?? 0;
        var games = new List<GameDisplayRow>(gameCount);
        progress?.Report(new("Checking games", 0, gameCount));
        IReadOnlySet<string> installedEffects = new HashSet<string>();
        IReadOnlyDictionary<string, string> packageSources = new Dictionary<string, string>();
        string? shaderInspectionError = null;

        if (gameCount > 0)
        {
            try
            {
                installedEffects = PresetShaderCheck.InstalledEffects(state!, cancellationToken);
                packageSources = PresetShaderCheck.PackageSources(state!, packages, cancellationToken);
            }
            catch (Exception error) when (
                error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or System.Security.SecurityException)
            {
                shaderInspectionError = "Unable to check central shader packages: " + error.Message;
            }
        }

        foreach (GameProfile game in state?.Games ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            games.Add(new(
                game.Id,
                InstallationInspection.GetInstallationWarning(game),
                shaderInspectionError ?? PresetShaderCheck.GetWarning(
                    game, installedEffects, packageSources, cancellationToken),
                Path.GetFileName(game.Executable),
                game.Api == RenderApi.DXGI ? "DXGI" : DisplayNames.For(game.Api),
                game.Build == RuntimeBuild.FullAddon ? "Standard + add-on" : DisplayNames.For(game.Build),
                game.Architecture == CpuArchitecture.X86 ? "32-bit" : "64-bit",
                game.Executable));

            if (reportWatch.ElapsedMilliseconds >= 100 || games.Count == gameCount)
            {
                progress?.Report(new("Checking games", games.Count, gameCount));
                reportWatch.Restart();
            }
        }

        return games;
    }

    private ViewPresentation PrepareView(
        CancellationToken cancellationToken,
        IProgress<StorageLoadProgress>? progress,
        IReadOnlyList<ShaderPackage> packages)
    {
        ManagerState? state = storage.State;
        List<GameDisplayRow> games = PrepareGames(state, cancellationToken, progress, packages);
        var reportWatch = Stopwatch.StartNew();

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new("Checking runtime and Vulkan"));
        Dictionary<RuntimeBuild, string> versions = Enum.GetValues<RuntimeBuild>()
            .ToDictionary(build => build, build => GetInstalledRuntimeVersion(state, build));
        VulkanInspection inspection = vulkan.Inspect(state);
        var completeness = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (InstalledPackage package in state?.Packages ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            completeness[package.Id] = ShaderFiles.IsPackageComplete(package, state!.Paths);

            if (reportWatch.ElapsedMilliseconds >= 100 || completeness.Count == state.Packages.Count)
            {
                progress?.Report(new("Checking shader packages", completeness.Count, state.Packages.Count));
                reportWatch.Restart();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new(state, games, versions, inspection, completeness);
    }

    private async Task RefreshGameWarningsAsync()
    {
        ViewPresentation? current = presentation;
        if (operationCancellation != null || storageLoadFailed || closeAfterOperation
            || current?.State == null || IsDisposed || Disposing)
        {
            return;
        }

        gameWarningRefreshCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        gameWarningRefreshCancellation = cancellation;
        List<ShaderPackage> currentCatalog = catalog;

        try
        {
            List<GameDisplayRow> checkedGames = await Task.Run(
                () => PrepareGames(current.State, cancellation.Token, null, currentCatalog),
                cancellation.Token);

            if (cancellation.IsCancellationRequested || IsDisposed || Disposing || closeAfterOperation
                || operationCancellation != null
                || !ReferenceEquals(presentation, current)
                || !ReferenceEquals(catalog, currentCatalog))
            {
                return;
            }

            var warnings = checkedGames.ToDictionary(game => game.Id);
            foreach (DataGridViewRow row in gamesGrid.Rows)
            {
                if (row.Cells["Id"].Value is string id && warnings.TryGetValue(id, out GameDisplayRow? game))
                {
                    row.Cells["InstallationWarning"].Value = game.InstallationWarning;
                    row.Cells["ShaderWarning"].Value = game.ShaderWarning;
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            WriteLog("Could not refresh game warnings: " + error.Message);
        }
        finally
        {
            if (ReferenceEquals(gameWarningRefreshCancellation, cancellation))
            {
                gameWarningRefreshCancellation = null;
            }
        }
    }

    private async Task RefreshViewAsync(
        CancellationToken cancellationToken,
        IProgress<StorageLoadProgress>? progress = null)
    {
        List<ShaderPackage> currentCatalog = catalog;
        ViewPresentation prepared = await Task.Run(
            () => PrepareView(cancellationToken, progress, currentCatalog),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsDisposed && !Disposing)
        {
            storageLoading.Report(new("Preparing the grid"));
            storageLoading.Update();
            ApplyView(prepared);
        }
    }

    private void RefreshView() => ApplyView(PrepareView(CancellationToken.None, null, catalog));

    private void ApplyView(ViewPresentation prepared)
    {
        presentation = prepared;
        ManagerState? state = prepared.State;
        Dictionary<RuntimeBuild, string> runtimeVersions = prepared.RuntimeVersions;
        versionsLabel.Text = string.Join("   \u2022   ",
            runtimeVersions.Select(item => $"{DisplayNames.For(item.Key)}: {item.Value}"));
        runtimeStatusLabel.Text = $"Standard: {runtimeVersions[RuntimeBuild.Standard]}   |   " +
            $"Add-on: {runtimeVersions[RuntimeBuild.FullAddon]}";
        RefreshRuntimeUpdateNotice();
        gamesGrid.DataSource = prepared.Games;
        storageLabel.Text = state?.Paths.Root ?? "Not configured";

        if (state != null)
        {
            vulkanBuildSelector.SelectedItem = state.VulkanBuild;
        }

        RefreshVulkanStatus(prepared.Vulkan);
        FilterPackages();
    }

    private static string GetInstalledRuntimeVersion(ManagerState? state, RuntimeBuild build)
    {
        if (state == null)
        {
            return "Not installed";
        }

        string directory = state.Paths.Binaries(build);
        string[] binaries = [Path.Combine(directory, "ReShade32.dll"), Path.Combine(directory, "ReShade64.dll")];
        if (!binaries.Any(File.Exists))
        {
            return "Not installed";
        }

        if (!binaries.All(File.Exists))
        {
            return "Incomplete";
        }

        try
        {
            string[] versions = binaries
                .Select(path =>
                    FileVersionInfo.GetVersionInfo(path).ProductVersion?.Split('+')[0] ?? "Unknown version")
                .Distinct()
                .ToArray();
            return versions.Length == 1 ? versions[0] : "Version mismatch";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "Unavailable";
        }
    }
}
