namespace ReShadeCentralized;

public sealed partial class Manager
{
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
                        ". In Shader packages, select those packages and apply the selection again. " +
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

            progress?.Report("Central shader folder purged. Choose Apply selection to reinstall packages.");
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
                "Existing files were retained. In Shader packages, select this package and apply the selection again. " +
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
}
