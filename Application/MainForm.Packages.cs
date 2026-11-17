namespace ReShadeCentralized;

public sealed partial class MainForm {
    private readonly PackagesGrid packageList = new();

    private readonly TextBox packageSearchBox = new() {
        PlaceholderText = "Search shader packages\u2026"
    };

    private readonly HashSet<string> selectedPackageIds = new(StringComparer.OrdinalIgnoreCase);
    private Button? applySelectionButton;
    private List<ShaderPackage> catalog = [];

    private void BuildPackages() {
        TableLayoutPanel layout = CreatePage("Shader packages");
        packageSearchBox.Dock = DockStyle.Fill;
        packageSearchBox.Margin = new Padding(0, 0, 0, 12);
        packageSearchBox.AccessibleName = "Search shader packages";
        layout.Controls.Add(packageSearchBox, 0, 0);

        packageList.Font = DarkTheme.BodyFont;
        packageList.Margin = Padding.Empty;
        layout.Controls.Add(packageList, 0, 1);

        FlowLayoutPanel commands = CreateCommandBar();
        commands.WrapContents = true;
        commands.Margin = new Padding(0, 12, 0, 0);
        applySelectionButton = CreateActionButton(
            "Apply selection",
            ApplyPackagesAsync,
            primary: true,
            isAvailable: () => catalog.Count > 0
                && (selectedPackageIds.Count > 0 || storage.State is { Packages.Count: > 0 }));
        commands.Controls.Add(applySelectionButton);
        commands.Controls.Add(CreateActionButton("Refresh catalog", OnRefreshCatalogAsync));
        commands.Controls.Add(CreateActionButton("Purge shader folder...", OnPurgeShaderFolderAsync,
            isAvailable: () => storage.State != null));
        layout.Controls.Add(commands, 0, 2);

        packageSearchBox.TextChanged += (_, _) => FilterPackages();
        packageList.PackageChecked += (package, selected) => {
            if (selected)
                selectedPackageIds.Add(package.Id);
            else
                selectedPackageIds.Remove(package.Id);

            List<ShaderPackage> packages = DisplayedPackages(catalog);
            selectedPackageIds.UnionWith(ShaderDependencyIds(packages));
            packageList.SetChecks(selectedPackageIds, packages);
            UpdateActionAvailability();
        };
    }

    private List<ShaderPackage> DisplayedPackages(IReadOnlyList<ShaderPackage> availableCatalog) {
        var packages = new List<ShaderPackage>(availableCatalog);
        if (storage.State is { Packages.Count: > 0 } state) {
            var catalogIds = availableCatalog.Select(package => package.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            packages.AddRange(state.Packages.Where(installed => !catalogIds.Contains(installed.Id))
                .Select(ShaderLibrary.PackageDescriptor));
        }

        return packages;
    }

    private IEnumerable<string> ShaderDependencyIds(IReadOnlyList<ShaderPackage> packages) {
        ShaderPackage[] selected = packages.Where(package => selectedPackageIds.Contains(package.Id)).ToArray();
        return ShaderDependencies.WithDependencies(selected, packages, (_, _) => { }).Select(package => package.Id);
    }

    private void SynchronizePackageChoices() {
        selectedPackageIds.Clear();
        foreach (string id in storage.State?.SelectedPackages ?? []) {
            selectedPackageIds.Add(id);
        }

        if (selectedPackageIds.Count == 0) {
            foreach (InstalledPackage installed in storage.State?.Packages ?? []) {
                selectedPackageIds.Add(installed.Id);
            }

            foreach (ShaderPackage package in catalog.Where(package => package.Required || package.Enabled)) {
                selectedPackageIds.Add(package.Id);
            }
        }

        List<ShaderPackage> packages = DisplayedPackages(catalog);
        selectedPackageIds.UnionWith(packages.Where(package => package.InstalledOnly).Select(package => package.Id));
        selectedPackageIds.UnionWith(ShaderDependencyIds(packages));
    }

    private void FilterPackages() {
        packageList.SetPackages(
            DisplayedPackages(catalog),
            packageSearchBox.Text,
            selectedPackageIds,
            storage.State,
            ReferenceEquals(presentation?.State, storage.State) ? presentation?.PackageCompleteness : null);
        UpdateActionAvailability();
    }

    private async Task ApplyPackagesAsync() {
        packageList.EndEdit();
        List<ShaderPackage> currentCatalog = catalog;
        ShaderSelectionPlan plan = shaders.CreateShaderSelectionPlan(
            DisplayedPackages(currentCatalog).Where(package => selectedPackageIds.Contains(package.Id)).ToArray(),
            currentCatalog);

        if (plan.Removals.Count > 0 && MessageBox.Show(
            this,
            "Remove these shader packages and all files inside their central shader and texture folders, " +
            "including custom or modified files?\n\n" +
            string.Join("\n", plan.Removals.Select(package => package.Name)) +
            "\n\nClose affected games first. Presets and game-local shaders will be kept.",
            "Remove shader packages",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        try {
            await RunOperationAsync("Applying shader selection", async cancellationToken => {
                PackageUpdateReport report = await shaders.ApplyShaderSelectionAsync(
                    plan,
                    currentCatalog,
                    progress,
                    cancellationToken,
                    removalsConfirmed: true);
                WriteLog(report.Summary);

                if (report.FailedCount > 0) {
                    tabs.SelectedIndex = 3;
                    MessageBox.Show(
                        this,
                        report.Summary +
                        "\n\nCompleted changes were kept. Check Activity for failures, then apply the selection again.",
                        "Shader packages",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            }, reportCompletion: false);
        } finally {
            FilterPackages();
        }
    }

    private Task OnRefreshCatalogAsync() {
        packageList.EndEdit();
        return RunOperationAsync("Refreshing catalog", async cancellationToken => {
            List<ShaderPackage> refreshed = await shaderCatalog.RefreshAsync(progress, cancellationToken);
            List<ShaderPackage> displayed = DisplayedPackages(refreshed);
            IEnumerable<string> dependencies = ShaderDependencyIds(displayed);
            cancellationToken.ThrowIfCancellationRequested();
            catalog = refreshed;
            selectedPackageIds.IntersectWith(displayed.Select(package => package.Id));
            selectedPackageIds.UnionWith(dependencies);
        });
    }

    private async Task OnPurgeShaderFolderAsync() {
        string root = storage.RequireState().Paths.ShaderRoot;
        DialogResult answer = MessageBox.Show(
            this,
            "Delete all central shaders and textures, including custom and unmanaged files?\n\n" + root +
            "\n\nPresets, profiles and game-local shaders will be kept. Close affected games first. " +
            "After purging, choose Apply selection to reinstall selected packages still in the catalog.",
            "Purge shader folder",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
            return;

        packageList.EndEdit();
        await RunOperationAsync("Purging central shader folder",
            cancellationToken => shaders.PurgeShaderFolderAsync(progress, cancellationToken));
    }
}
