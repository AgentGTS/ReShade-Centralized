namespace ReShadeCentralized;

public sealed partial class MainForm
{
    private void LoadCatalog()
    {
        catalog = manager.Shaders.CachedCatalog();
        SynchronizePackageChoices();
        FilterPackages();
    }

    private void BuildPackages()
    {
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
            "Apply selection", ApplyPackagesAsync, primary: true,
            isAvailable: () => selectedPackageIds.Count > 0 || manager.State is { Packages.Count: > 0 });
        commands.Controls.Add(applySelectionButton);
        commands.Controls.Add(CreateActionButton("Refresh catalog", OnRefreshCatalogAsync));
        commands.Controls.Add(CreateActionButton("Purge shader folder...", OnPurgeShaderFolderAsync,
            isAvailable: () => manager.State != null));
        layout.Controls.Add(commands, 0, 2);
        packageSearchBox.TextChanged += (_, _) => FilterPackages();
        packageList.PackageChecked += (package, selected) =>
        {
            if (selected)
            {
                selectedPackageIds.Add(package.Id);
            }
            else
            {
                selectedPackageIds.Remove(package.Id);
            }
            ExpandShaderDependencies();
            packageList.SetChecks(selectedPackageIds, AvailablePackages());
            UpdateActionAvailability();
        };
    }

    private List<ShaderPackage> AvailablePackages() => catalog.Concat(
        (manager.State?.Packages ?? []).Where(installed => !catalog.Any(package => package.Id == installed.Id))
            .Select(Manager.PackageDescriptor)).ToList();

    private void ExpandShaderDependencies()
    {
        List<ShaderPackage> packages = AvailablePackages();
        ShaderPackage[] selected = packages.Where(package => selectedPackageIds.Contains(package.Id)).ToArray();
        foreach (ShaderPackage dependency in ShaderService.WithDependencies(selected, packages, (_, _) => { }))
        {
            selectedPackageIds.Add(dependency.Id);
        }
    }

    private void SynchronizePackageChoices()
    {
        selectedPackageIds.Clear();
        foreach (string id in manager.State?.SelectedPackages ?? [])
        {
            selectedPackageIds.Add(id);
        }
        if (selectedPackageIds.Count == 0)
        {
            foreach (InstalledPackage installed in manager.State?.Packages ?? [])
            {
                selectedPackageIds.Add(installed.Id);
            }
            foreach (ShaderPackage package in catalog.Where(package => package.Required || package.Enabled))
            {
                selectedPackageIds.Add(package.Id);
            }
        }
        ExpandShaderDependencies();
    }

    private void FilterPackages()
    {
        List<ShaderPackage> packages = AvailablePackages();
        packageList.SetPackages(packages.Where(package =>
            $"{package.Name} {package.Description}".Contains(packageSearchBox.Text, StringComparison.OrdinalIgnoreCase)),
            selectedPackageIds, manager.State,
            ReferenceEquals(presentation?.State, manager.State) ? presentation?.PackageCompleteness : null);
        packageList.SetChecks(selectedPackageIds, packages);
        UpdateActionAvailability();
    }

    private void ResetPackageChoices()
    {
        packageList.CancelEdit();
        SynchronizePackageChoices();
        FilterPackages();
    }

    private async Task ApplyPackagesAsync()
    {
        packageList.EndEdit();
        List<ShaderPackage> packages = AvailablePackages();
        ShaderSelectionPlan plan = manager.CreateShaderSelectionPlan(
            packages.Where(package => selectedPackageIds.Contains(package.Id)).ToArray(), packages);
        try
        {
            await RunOperationAsync("Applying shader selection", async cancellationToken =>
            {
                PackageUpdateReport report = await manager.ApplyShaderSelectionAsync(
                    plan, packages, progress, cancellationToken, removalsConfirmed: true);
                WriteLog(report.Summary);
                if (report.FailedCount > 0)
                {
                    tabs.SelectedIndex = 3;
                    MessageBox.Show(this, report.Summary +
                        "\n\nCompleted changes were kept. Check Activity for failures, then apply the selection again.",
                        "Shader packages", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }, reportCompletion: false);
        }
        finally
        {
            FilterPackages();
        }
    }

    private Task OnRefreshCatalogAsync()
    {
        ResetPackageChoices();
        return RunOperationAsync("Refreshing catalog", async cancellationToken =>
        {
            catalog = await manager.Shaders.CatalogAsync(progress, cancellationToken);
            ResetPackageChoices();
        });
    }

    private async Task OnPurgeShaderFolderAsync()
    {
        string root = RequireState().Paths.ShaderRoot;
        ResetPackageChoices();
        DialogResult answer = MessageBox.Show(this,
            "Delete all central shaders and textures, including custom and unmanaged files?\n\n" + root +
            "\n\nPresets, profiles and game-local shaders will be kept. Close affected games first. " +
            "After purging, choose Apply selection to reinstall your selected packages.",
            "Purge shader folder", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        await RunOperationAsync("Purging central shader folder",
            cancellationToken => manager.PurgeShaderFolderAsync(progress, cancellationToken));
        ResetPackageChoices();
    }
}
