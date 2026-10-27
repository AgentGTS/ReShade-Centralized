namespace ReShadeCentralized;

public sealed partial class MainForm
{
    private void BuildGames()
    {
        TableLayoutPanel layout = CreatePage("Games");
        FlowLayoutPanel commands = CreateCommandBar();
        commands.Controls.Add(CreateActionButton("Install game...", () => ShowGameDialogAsync("Installing game"), primary: true));
        commands.Controls.Add(CreateActionButton("Repair / edit...", EditSelectedAsync,
            isAvailable: () => gamesGrid.SelectedRows.Count > 0));
        commands.Controls.Add(CreateActionButton("Unregister", OnRemoveSelectedAsync,
            isAvailable: () => gamesGrid.SelectedRows.Count > 0));
        commands.Controls.Add(CreateActionButton("Repair all", OnRepairAllAsync,
            isAvailable: () => gamesGrid.Rows.Count > 0));
        commands.Controls.Add(CreateActionButton("Open profile", OnOpenProfileAsync,
            isAvailable: () => gamesGrid.SelectedRows.Count > 0));
        commands.Controls.Add(CreateActionButton("Open game folder", OnOpenGameFolderAsync,
            isAvailable: () => gamesGrid.SelectedRows.Count > 0 &&
                gamesGrid.SelectedRows[0].Cells["Executable"].Value is string executable &&
                Path.IsPathFullyQualified(executable)));
        gamesGrid.SelectionChanged += (_, _) => UpdateActionAvailability();
        gamesGrid.DataBindingComplete += (_, _) => UpdateActionAvailability();
        layout.Controls.Add(commands, 0, 0);
        layout.Controls.Add(gamesGrid, 0, 1);
        gamesGrid.Controls.Add(storageLoading);
        gamesGrid.Resize += (_, _) => PositionStorageLoading();
        gamesGrid.ColumnHeadersHeightChanged += (_, _) => PositionStorageLoading();
        PositionStorageLoading();
        gamesGrid.Margin = Padding.Empty;
        gamesGrid.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex >= 0)
            {
                try
                {
                    await EditSelectedAsync();
                }
                catch (Exception error)
                {
                    ReportError(error);
                }
            }
        };
    }

    private GameProfile GetSelectedGame()
    {
        string? id = gamesGrid.CurrentRow?.Cells["Id"].Value as string;
        return RequireState().Games.SingleOrDefault(game => game.Id == id) ??
            throw new InvalidOperationException("No game is selected. Open Games and select a row. " +
                "To add a game first, choose Install game.");
    }

    private Task EditSelectedAsync() => ShowGameDialogAsync("Repairing game", GetSelectedGame());

    private async Task ShowGameDialogAsync(string description, GameProfile? game = null)
    {
        ManagerState state = RequireState();
        using var dialog = new GameDialog(state.VulkanBuild, game, state.Games, state: state);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            await RunOperationAsync(
                description,
                cancellationToken => manager.InstallAsync(
                    dialog.Result(), cancellationToken, dialog.ReviewedPlan, dialog.ReplacementConfirmed));
        }
    }

    private async Task OnRemoveSelectedAsync()
    {
        GameProfile game = GetSelectedGame();
        if (MessageBox.Show(
            this,
            "Unregister " + game.Name + "? Presets, profile files, and local shaders are kept. " +
                "Files changed by other tools are left in place.",
            "Unregister ReShade",
            MessageBoxButtons.OKCancel) == DialogResult.OK)
        {
            await RunOperationAsync(
                "Unregistering game",
                async cancellationToken =>
                {
                    UnregisterResult result = await manager.UnregisterAsync(game.Id, cancellationToken);
                    foreach (string path in result.PreservedFiles)
                    {
                        WriteLog("Preserved externally changed file: " + path);
                    }
                });
        }
    }

    private Task OnRepairAllAsync()
    {
        return RunOperationAsync(
            "Repairing managed installations",
            cancellationToken => manager.RepairAsync(progress, cancellationToken));
    }

    private Task OnOpenProfileAsync()
    {
        OpenFolder(GetSelectedGame().Directory);
        return Task.CompletedTask;
    }

    private Task OnOpenGameFolderAsync()
    {
        GameProfile game = GetSelectedGame();
        string directory = Path.GetDirectoryName(game.Executable)!;
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"The game folder no longer exists: {directory}. " +
                "Choose Games > Repair / edit and select the game's current executable.");
        }

        OpenFolder(directory);
        return Task.CompletedTask;
    }
}
