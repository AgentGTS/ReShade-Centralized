using System.Diagnostics;

namespace ReShadeCentralized;

public sealed partial class MainForm {
    private readonly GamesGrid gamesGrid = new() {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
        RowHeadersVisible = false
    };

    private CancellationTokenSource? gameWarningRefreshCancellation;

    private void BuildGames() {
        TableLayoutPanel layout = CreatePage("Games");
        FlowLayoutPanel commands = CreateCommandBar();
        commands.Controls.Add(CreateActionButton(
            "Install game...", () => ShowGameDialogAsync("Installing game"), primary: true));
        commands.Controls.Add(CreateActionButton(
            "Repair / edit...", EditSelectedAsync,
            isAvailable: () => gamesGrid.SelectedRows.Count > 0));
        commands.Controls.Add(CreateActionButton(
            "Unregister", OnRemoveSelectedAsync,
            isAvailable: () => gamesGrid.SelectedRows.Count > 0));
        commands.Controls.Add(CreateActionButton(
            "Open profile", () => OpenFolder(GetSelectedGame().Directory),
            isAvailable: () => gamesGrid.SelectedRows.Count > 0));
        commands.Controls.Add(CreateActionButton(
            "Open game folder", OnOpenGameFolderAsync,
            isAvailable: () => gamesGrid.SelectedRows.Count > 0
                && gamesGrid.SelectedRows[0].Cells["Executable"].Value is string executable
                && Path.IsPathFullyQualified(executable)));

        gamesGrid.SelectionChanged += (_, _) => UpdateActionAvailability();
        gamesGrid.DataBindingComplete += (_, _) => UpdateActionAvailability();
        Activated += async (_, _) => await RefreshGameWarningsAsync();
        FormClosed += (_, _) => gameWarningRefreshCancellation?.Cancel();
        tabs.SelectedIndexChanged += async (_, _) => {
            if (tabs.SelectedIndex == 0)
                await RefreshGameWarningsAsync();
        };

        layout.Controls.Add(commands, 0, 0);
        layout.Controls.Add(gamesGrid, 0, 1);
        gamesGrid.Controls.Add(storageLoading);
        gamesGrid.Resize += (_, _) => PositionStorageLoading();
        gamesGrid.ColumnHeadersHeightChanged += (_, _) => PositionStorageLoading();
        PositionStorageLoading();
        gamesGrid.Margin = Padding.Empty;

        gamesGrid.CellDoubleClick += async (_, e) => {
            if (e.RowIndex >= 0) {
                try {
                    await EditSelectedAsync();
                } catch (Exception error) {
                    ReportError(error);
                }
            }
        };
    }

    private GameProfile GetSelectedGame() {
        string? id = gamesGrid.SelectedRows.Count == 1
            ? gamesGrid.SelectedRows[0].Cells["Id"].Value as string
            : null;
        return storage.RequireState().Games.SingleOrDefault(game => game.Id == id) ??
            throw new InvalidOperationException(
                "No game is selected. Open Games and select a row. " +
                "To add a game first, choose Install game.");
    }

    private Task EditSelectedAsync() => ShowGameDialogAsync("Repairing game", GetSelectedGame());

    private async Task ShowGameDialogAsync(string description, GameProfile? game = null) {
        ManagerState state = storage.RequireState();
        using var dialog = new GameDialog(state, game);

        if (dialog.ShowDialog(this) == DialogResult.OK) {
            await RunOperationAsync(
                description,
                cancellationToken => games.InstallAsync(
                    dialog.Result(),
                    cancellationToken,
                    dialog.ReviewedPlan,
                    dialog.ReplacementConfirmed));
        }
    }

    private async Task OnRemoveSelectedAsync() {
        GameProfile game = GetSelectedGame();

        if (MessageBox.Show(
            this,
            "Unregister " + game.Name + "?\n\nClose the game first. This removes its verified injector and config links " +
                "from the game folder, and deletes its selected and default presets inside its central profile.\n\n" +
                "External presets, other profile files, local shaders and shared runtimes are kept. " +
                "Files changed by other tools are preserved and reported.",
            "Unregister ReShade",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.OK) {
            await RunOperationAsync(
                "Unregistering game",
                async cancellationToken => {
                    UnregisterResult result = await games.UnregisterAsync(game.Id, cancellationToken);
                    foreach (string path in result.PreservedFiles) {
                        WriteLog(
                            "Preserved file; deletion was outside the unregister scope or could not be validated: " + path);
                    }

                    if (result.PreservedFiles.Count > 0) {
                        MessageBox.Show(
                            this,
                            "The game was unregistered. These files were kept because they are external, shared, " +
                                "changed, or could not be safely validated:\n\n" +
                                string.Join("\n", result.PreservedFiles) +
                                "\n\nA retained injector or game config may still load ReShade. Review those files before launching the game.",
                            "Unregister: files preserved",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                    }
                });
        }
    }

    private Task OnOpenGameFolderAsync() {
        GameProfile game = GetSelectedGame();
        string directory = Path.GetDirectoryName(game.Executable)!;

        if (!Directory.Exists(directory)) {
            throw new DirectoryNotFoundException($"The game folder no longer exists: {directory}. " +
                "Choose Games > Repair / edit and select the game's current executable.");
        }

        return OpenFolder(directory);
    }

    private void PositionStorageLoading() {
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

    private void ApplyGames(List<GameDisplayRow> rows) {
        if (gamesGrid.DataSource is not List<GameDisplayRow> current) {
            gamesGrid.DataSource = rows;
            return;
        }

        if (current.SequenceEqual(rows))
            return;

        static string? GameId(DataGridViewRow row) => (row.DataBoundItem as GameDisplayRow)?.Id;

        GridViewport viewport = GridViewport.Capture(gamesGrid, GameId);
        gamesGrid.DataSource = rows;
        viewport.Restore(gamesGrid, GameId);
    }

    private static List<GameDisplayRow> PrepareGames(
        ManagerState? state,
        CancellationToken cancellationToken,
        IProgress<StorageLoadProgress>? progress,
        IReadOnlyList<ShaderPackage> packages) {
        var reportWatch = Stopwatch.StartNew();
        int gameCount = state?.Games.Count ?? 0;
        var games = new List<GameDisplayRow>(gameCount);
        progress?.Report(new("Checking games", 0, gameCount));
        IReadOnlySet<string> installedEffects = new HashSet<string>();
        IReadOnlyDictionary<string, string> packageSources = new Dictionary<string, string>();
        string? shaderInspectionError = null;

        if (gameCount > 0) {
            try {
                installedEffects = PresetShaderCheck.InstalledEffects(state!, cancellationToken);
                packageSources = PresetShaderCheck.PackageSources(state!, packages, cancellationToken);
            } catch (Exception error) when (
                error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or System.Security.SecurityException) {
                shaderInspectionError = "Unable to check central shader packages: " + error.Message;
            }
        }

        foreach (GameProfile game in state?.Games ?? []) {
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

            if (reportWatch.ElapsedMilliseconds >= 100 || games.Count == gameCount) {
                progress?.Report(new("Checking games", games.Count, gameCount));
                reportWatch.Restart();
            }
        }

        return games;
    }

    private async Task RefreshGameWarningsAsync() {
        ViewPresentation? current = presentation;
        if (operationCancellation != null || storageLoadFailed || closeAfterOperation
            || current?.State == null || IsDisposed || Disposing)
            return;

        gameWarningRefreshCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        gameWarningRefreshCancellation = cancellation;
        List<ShaderPackage> currentCatalog = catalog;

        try {
            List<GameDisplayRow> checkedGames = await Task.Run(
                () => PrepareGames(current.State, cancellation.Token, null, currentCatalog),
                cancellation.Token);

            if (cancellation.IsCancellationRequested || IsDisposed || Disposing || closeAfterOperation
                || operationCancellation != null
                || !ReferenceEquals(presentation, current)
                || !ReferenceEquals(catalog, currentCatalog))
                return;

            var warnings = checkedGames.ToDictionary(game => game.Id);
            foreach (DataGridViewRow row in gamesGrid.Rows) {
                if (row.Cells["Id"].Value is string id && warnings.TryGetValue(id, out GameDisplayRow? game)) {
                    row.Cells["InstallationWarning"].Value = game.InstallationWarning;
                    row.Cells["ShaderWarning"].Value = game.ShaderWarning;
                }
            }
        } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
        } catch (Exception error) {
            WriteLog("Could not refresh game warnings: " + error.Message);
        } finally {
            if (ReferenceEquals(gameWarningRefreshCancellation, cancellation))
                gameWarningRefreshCancellation = null;
        }
    }
}
