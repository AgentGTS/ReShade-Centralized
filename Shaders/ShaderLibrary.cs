using System.Text.Json;

namespace ReShadeCentralized;

public sealed class ShaderLibrary(CentralStorage storage, Downloads downloads) {
    private async Task<PackageUpdateReport> UpdatePackagesAsync(
        ShaderSelectionPlan reviewedPlan,
        IReadOnlyList<ShaderPackage> catalog,
        IProgress<string>? progress,
        CancellationToken cancellationToken) {
        var report = new PackageUpdateReport();

        void ReportResult((bool Success, string Message) result, bool removal = false) {
            if (!result.Success)
                report.FailedCount++;
            else if (removal)
                report.RemovedCount++;
            else
                report.InstalledCount++;

            progress?.Report(result.Message);
        }

        await storage.RunAsync(async () => {
            ShaderSelectionPlan currentPlan = CreateShaderSelectionPlan(reviewedPlan.Selected, catalog);
            if (currentPlan.Fingerprint != reviewedPlan.Fingerprint
                || !currentPlan.Removals.Select(package => package.Id)
                    .SequenceEqual(
                        reviewedPlan.Removals.Select(package => package.Id),
                        StringComparer.OrdinalIgnoreCase))
                throw new IOException("Shader selections or installed packages changed. Review the removals again.");

            IReadOnlyList<ShaderPackage> selected = currentPlan.Selected;
            HashSet<string> selectedIds = selected.Select(package => package.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<ShaderPackage> packages = ShaderDependencies.WithDependencies(
                catalog.Where(package => selectedIds.Contains(package.Id)),
                catalog,
                (package, message) => ReportResult((false, $"Skipped {package.Name}: {message}")));
            string[] desired = selected.Concat(packages).Select(package => package.Id).Distinct().ToArray();
            SaveShaderChoices(desired, cancellationToken);

            foreach (ShaderPackage package in packages) {
                cancellationToken.ThrowIfCancellationRequested();
                ShaderPackage[] missingDependencies = ShaderDependencies.Dependencies(package, packages)
                    .Where(dependency => !storage.State!.Packages.Any(installed => installed.Id == dependency.Id
                        && ShaderFiles.IsPackageComplete(installed, storage.State.Paths)))
                    .ToArray();
                if (missingDependencies.Length > 0) {
                    ReportResult((false, $"Skipped {package.Name}: required packages are unavailable: " +
                        string.Join(", ", missingDependencies.Select(dependency => dependency.Name)) +
                        ". In Shader packages, select those packages and apply the selection again. " +
                        "Existing files were retained."));
                    continue;
                }

                ReportResult(await UpdatePackageAsync(package, desired, progress, cancellationToken));
            }

            foreach (InstalledPackage removal in OrderShaderRemovals(currentPlan.Removals, catalog)) {
                cancellationToken.ThrowIfCancellationRequested();
                ReportResult(RemoveShaderPackage(removal, desired, catalog, cancellationToken), removal: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            SaveShaderChoices(selected.Concat(catalog.Where(package => package.Required))
                .Concat(packages).Select(package => package.Id).Distinct(), cancellationToken);

            foreach (string duplicate in ShaderFiles.DuplicateEffects(storage.State!)) {
                progress?.Report("Duplicate effect filename: " + duplicate);
            }
        }, cancellationToken);
        return report;
    }

    public Task PurgeShaderFolderAsync(IProgress<string>? progress, CancellationToken cancellationToken) {
        return storage.RunAsync(() => {
            ManagerState updatedState = storage.RequireState().Clone();
            string root = updatedState.Paths.ShaderRoot;
            string[] roots = [root];
            RemoveShaderFiles(updatedState, roots, "Purge central shader folder", transaction => {
                updatedState.Packages.Clear();
                foreach (GameProfile game in updatedState.Games) {
                    cancellationToken.ThrowIfCancellationRequested();
                    ConfigurationService.Update(updatedState, game, transaction, removedSearchRoots: roots);
                }
            }, cancellationToken,
                count => progress?.Report($"Purging {count} files from {root}"));

            progress?.Report("Central shader folder purged. Choose Apply selection to reinstall packages.");
            return Task.CompletedTask;
        }, cancellationToken);
    }

    private static void RemoveEmptyShaderFolders(string directory) {
        LocalFiles.CheckParents(Path.Combine(directory, "check"));
        if (!Directory.Exists(directory))
            return;

        foreach (string child in Directory.EnumerateDirectories(directory)) {
            RemoveEmptyShaderFolders(child);
        }

        Directory.Delete(directory, recursive: false);
    }

    private async Task<(bool Installed, string Message)> UpdatePackageAsync(
        ShaderPackage package,
        IReadOnlyList<string> selection,
        IProgress<string>? progress,
        CancellationToken cancellationToken) {
        StagingDirectory? staging = null;
        try {
            staging = new StagingDirectory(storage.Store, warning => progress?.Report(warning));
            ManagerState updatedState = storage.State!.Clone();
            InstalledPackage? installed = updatedState.Packages.SingleOrDefault(item => item.Id == package.Id);
            if (installed != null)
                ShaderFiles.CheckUnmodified(installed, updatedState.Paths, cancellationToken);

            progress?.Report("Updating " + package.Name);
            Directory.CreateDirectory(staging.Path);
            string zip = Path.Combine(staging.Path, "package.zip");
            await downloads.FileAsync(package.DownloadUrl, zip, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            string source = Path.Combine(staging.Path, "extracted");
            SafeArchive.Extract(zip, source, cancellationToken);

            using FileTransaction transaction = storage.BeginTransaction("Update shader package: " + package.Name);
            ShaderFiles.InstallExtracted(package, source, updatedState, transaction, cancellationToken);
            foreach (GameProfile game in updatedState.Games) {
                ConfigurationService.Update(updatedState, game, transaction);
            }

            updatedState.SelectedPackages = [.. selection];

            storage.Commit(updatedState, transaction, cancellationToken);
            return (true, "Installed: " + package.Name);
        } catch (Exception error) when (error is IOException
            or HttpRequestException
            or InvalidDataException
            or UnauthorizedAccessException
            || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested)) {
            string explanation = error is InvalidDataException ?
                "The package archive is invalid or unsupported. Open Shader packages > Refresh catalog, " +
                "then retry. Details: " + error.Message : UserErrors.Describe(error);
            return (false, $"Failed {package.Name}: {explanation}\n" +
                "Existing files were retained. In Shader packages, select this package and apply the selection again. " +
                $"Repository: {package.Repository}");
        } finally {
            if (staging != null) {
                try {
                    staging.Dispose();
                } catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
                    string warning = $"Could not remove temporary files for {package.Name}: {staging.Path}. " +
                        "After this operation finishes, close programs using this folder and remove it " +
                        "to reclaim disk space. " + error.Message;
                    progress?.Report(warning);
                }
            }
        }
    }

    public static ShaderPackage PackageDescriptor(InstalledPackage installed) => new() {
        Id = installed.Id,
        Name = installed.Name,
        Repository = installed.Repository,
        InstalledOnly = true
    };

    public ShaderSelectionPlan CreateShaderSelectionPlan(
        IReadOnlyList<ShaderPackage> selected,
        IReadOnlyList<ShaderPackage> catalog) {
        ManagerState state = storage.RequireState();
        if (!catalog.Any(package => package.Required)) {
            throw new InvalidDataException("Refresh the shader catalog before applying a selection; " +
                "required effects are missing.");
        }
        HashSet<string> requested = selected.Select(package => package.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> listed = catalog.Select(package => package.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ShaderPackage[] selectedCatalog = catalog.Where(package => requested.Contains(package.Id)).ToArray();
        IEnumerable<ShaderPackage> retained = state.Packages
            .Where(package => requested.Contains(package.Id) && !listed.Contains(package.Id))
            .Select(PackageDescriptor);
        ShaderPackage[] desired = selectedCatalog
            .Concat(ShaderDependencies.WithDependencies(selectedCatalog, catalog, (_, _) => { }))
            .Concat(retained)
            .DistinctBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(package => package.Id, StringComparer.Ordinal)
            .ToArray();
        HashSet<string> ids = desired.Select(package => package.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        InstalledPackage[] removals = state.Packages.Where(package => !ids.Contains(package.Id))
            .OrderBy(package => package.Id, StringComparer.Ordinal)
            .Select(package => package.Clone())
            .ToArray();
        string input = state.Paths.Root + JsonSerializer.Serialize(desired, ManagerState.Json) +
            JsonSerializer.Serialize(removals, ManagerState.Json);
        string fingerprint = LocalFiles.HashText(input);
        return new(desired, removals, fingerprint);
    }

    public Task<PackageUpdateReport> ApplyShaderSelectionAsync(
        ShaderSelectionPlan plan,
        IReadOnlyList<ShaderPackage> catalog,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        bool removalsConfirmed = false) {
        if (plan.Removals.Count > 0 && !removalsConfirmed) {
            throw new InvalidOperationException("Review and confirm shader package removals " +
                "before applying the selection.");
        }

        return UpdatePackagesAsync(plan, catalog, progress, cancellationToken);
    }

    private void SaveShaderChoices(IEnumerable<string> desired, CancellationToken cancellationToken) {
        ManagerState updated = storage.State!.Clone();
        updated.SelectedPackages = [.. desired];
        using FileTransaction transaction = storage.BeginTransaction("Save shader package selection");
        storage.Commit(updated, transaction, cancellationToken);
    }

    private static IEnumerable<InstalledPackage> OrderShaderRemovals(
        IReadOnlyList<InstalledPackage> removals,
        IReadOnlyList<ShaderPackage> catalog) {
        var remaining = removals.ToList();
        while (remaining.Count > 0) {
            ShaderPackage[] descriptors = remaining.Select(installed =>
                catalog.SingleOrDefault(package => package.Id == installed.Id) ?? PackageDescriptor(installed))
                .ToArray();
            InstalledPackage next = remaining.FirstOrDefault(candidate => !descriptors.Any(other =>
                ShaderDependencies.Dependencies(other, descriptors).Any(dependency => dependency.Id == candidate.Id)))
                ?? throw new InvalidDataException("Shader package dependencies contain a cycle. " +
                    "No further packages were removed.");
            remaining.Remove(next);
            yield return next;
        }
    }

    private (bool Removed, string Message) RemoveShaderPackage(
        InstalledPackage package,
        IReadOnlyList<string> desired,
        IReadOnlyList<ShaderPackage> catalog,
        CancellationToken cancellationToken) {
        try {
            ShaderPackage[] retained = storage.State!.Packages.Select(installed =>
                catalog.SingleOrDefault(entry => entry.Id == installed.Id) ?? PackageDescriptor(installed))
                .ToArray();
            string[] consumers = retained.Where(other => ShaderDependencies.Dependencies(other, retained)
                .Any(dependency => dependency.Id == package.Id))
                .Select(other => other.Name)
                .ToArray();
            if (consumers.Length > 0)
                return (false, $"Kept {package.Name}: still required by {string.Join(", ", consumers)}.");

            ManagerState updated = storage.State!.Clone();
            string[] roots =
            [
                ShaderFiles.PackageDirectory(updated.Paths.Shaders, package.Id),
                ShaderFiles.PackageDirectory(updated.Paths.Textures, package.Id)
            ];
            RemoveShaderFiles(updated, roots, "Remove shader package: " + package.Name, transaction => {
                updated.Packages.RemoveAll(entry => entry.Id == package.Id);
                updated.SelectedPackages = [.. desired];
                foreach (GameProfile game in updated.Games) {
                    ConfigurationService.Update(updated, game, transaction, removedSearchRoots: roots);
                }
            }, cancellationToken);
            return (true, "Removed: " + package.Name);
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) {
            return (false, $"Failed to remove {package.Name}: {UserErrors.Describe(error)}\n" +
                "Package files were retained.");
        }
    }

    private void RemoveShaderFiles(
        ManagerState state,
        IReadOnlyList<string> roots,
        string description,
        Action<FileTransaction> updateState,
        CancellationToken cancellationToken,
        Action<int>? beforeDelete = null) {
        var files = new List<(string Path, string Hash)>();
        foreach (string root in roots) {
            LocalFiles.CheckParents(Path.Combine(root, "check"));
            if (LocalFiles.Link(root) != null || File.Exists(root))
                throw new IOException("A shader folder must be a regular directory: " + root);
            if (Directory.Exists(root)) {
                foreach (string file in ShaderFiles.EnumerateFiles(root)) {
                    cancellationToken.ThrowIfCancellationRequested();
                    files.Add((file, LocalFiles.Hash(file, cancellationToken)));
                }
            }
        }
        beforeDelete?.Invoke(files.Count);
        using (FileTransaction transaction = storage.BeginTransaction(description)) {
            foreach ((string file, string hash) in files) {
                cancellationToken.ThrowIfCancellationRequested();
                if (!transaction.DeleteIfUnchanged(file, expectedHash: hash))
                    throw new IOException("A shader file changed while it was being removed: " + file);
            }
            updateState(transaction);
            storage.Commit(state, transaction, cancellationToken);
        }
        foreach (string root in roots) {
            try {
                RemoveEmptyShaderFolders(root);
            } catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
                storage.ReportWarning(description + " completed, but empty folders could not be removed: " +
                    root + ". " + error.Message);
            }
        }
    }
}

public sealed class PackageUpdateReport {
    public int InstalledCount { get; internal set; }
    public int RemovedCount { get; internal set; }
    public int FailedCount { get; internal set; }
    public string Summary =>
        $"Shader packages: {InstalledCount} installed/updated, {RemovedCount} removed, {FailedCount} failed.";
}

public sealed record ShaderSelectionPlan(
    IReadOnlyList<ShaderPackage> Selected,
    IReadOnlyList<InstalledPackage> Removals,
    string Fingerprint);

public static class ShaderDependencies {
    public static IReadOnlyList<ShaderPackage> WithDependencies(
        IEnumerable<ShaderPackage> selected,
        IReadOnlyList<ShaderPackage> catalog,
        Action<ShaderPackage, string> dependencyUnavailable) {
        var result = selected.Concat(catalog.Where(package => package.Required))
            .DistinctBy(package => package.Id)
            .ToList();

        // The upstream catalog describes this dependency but has no structured dependency field.
        ShaderPackage? dependent = result.SingleOrDefault(package =>
            ShaderCatalog.CanonicalRepository(package.Repository).EndsWith("/yplebedev/bfbfx"));
        if (dependent != null) {
            ShaderPackage? framework = catalog.SingleOrDefault(package =>
                ShaderCatalog.CanonicalRepository(package.Repository).EndsWith("/zenteon/zenteonfx"));
            if (framework == null) {
                const string message = "BFBFX requires ZenteonFX, which is missing from the catalog. " +
                    "Open Shader packages > Refresh catalog, then apply the selection again.";
                result.Remove(dependent);
                dependencyUnavailable(dependent, message);
            } else if (!result.Any(package => package.Id == framework.Id))
                result.Add(framework);
        }

        return result.OrderByDescending(package => package.Required)
            .ThenBy(package => package.Id == dependent?.Id)
            .ThenBy(
                package => package.Id,
                StringComparer.Ordinal)
            .ToList();
    }

    public static IEnumerable<ShaderPackage> Dependencies(
        ShaderPackage package,
        IReadOnlyList<ShaderPackage> selection) {
        bool needsFramework = ShaderCatalog.CanonicalRepository(package.Repository).EndsWith("/yplebedev/bfbfx");
        return selection.Where(candidate => candidate.Id != package.Id
            && ((!package.Required && candidate.Required)
                || (needsFramework && ShaderCatalog.CanonicalRepository(candidate.Repository)
                    .EndsWith("/zenteon/zenteonfx"))));
    }
}
