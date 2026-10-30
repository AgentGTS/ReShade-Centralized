using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ReShadeCentralized;

public sealed class ShaderLibrary(CentralStorage storage, Downloads downloads)
{
    public async Task<PackageUpdateReport> UpdatePackagesAsync(
        IReadOnlyList<ShaderPackage> selected,
        IReadOnlyList<ShaderPackage> catalog,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        bool rememberSelection = true,
        ShaderSelectionPlan? reviewedPlan = null)
    {
        var report = new PackageUpdateReport();
        void Report(string message)
        {
            report.Messages.Add(message);
            progress?.Report(message);
        }

        await storage.RunAsync(async () =>
        {
            storage.RequireState();

            ShaderSelectionPlan? currentPlan = reviewedPlan == null ? null : CreateShaderSelectionPlan(selected, catalog);
            if (reviewedPlan != null && (currentPlan!.Fingerprint != reviewedPlan.Fingerprint ||
                !currentPlan.Removals.Select(package => package.Id)
                    .SequenceEqual(reviewedPlan.Removals.Select(package => package.Id), StringComparer.OrdinalIgnoreCase)))
            {
                throw new IOException("Shader selections or installed packages changed. Review the removals again.");
            }

            IReadOnlyList<ShaderPackage> packages = ShaderDependencies.WithDependencies(
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
                ShaderPackage[] missingDependencies = ShaderDependencies.Dependencies(package, packages)
                    .Where(dependency => !storage.State!.Packages.Any(installed => installed.Id == dependency.Id &&
                        ShaderFiles.IsPackageComplete(installed, storage.State.Paths))).ToArray();
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

            using FileTransaction choices = storage.BeginTransaction("Save shader package selection");
            cancellationToken.ThrowIfCancellationRequested();
            ManagerState finalState = storage.State!.Clone();
            if (rememberSelection)
            {
                finalState.SelectedPackages = selected.Concat(catalog.Where(package => package.Required))
                    .Concat(packages).Select(package => package.Id).Distinct().ToList();
            }

            storage.Commit(finalState, choices, cancellationToken);
            foreach (string duplicate in ShaderFiles.DuplicateEffects(finalState))
            {
                Report("Duplicate effect filename: " + duplicate);
            }
        }, cancellationToken);
        return report;
    }

    public Task PurgeShaderFolderAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        return storage.RunAsync(() =>
        {
            ManagerState updatedState = storage.RequireState().Clone();
            string root = updatedState.Paths.ShaderRoot;
            LocalFiles.CheckParents(Path.Combine(root, "check"));
            if (LocalFiles.Link(root) != null || File.Exists(root))
            {
                throw new IOException("The central shader folder must be a regular directory: " + root);
            }

            var files = new List<string>();
            if (Directory.Exists(root))
            {
                foreach (string file in ShaderFiles.EnumerateFiles(root))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    files.Add(file);
                }
            }

            progress?.Report($"Purging {files.Count} files from {root}");
            using (FileTransaction transaction = storage.BeginTransaction("Purge central shader folder"))
            {
                foreach (string file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    transaction.Delete(file);
                }

                updatedState.Packages.Clear();
                storage.Commit(updatedState, transaction, cancellationToken);
            }

            try
            {
                RemoveEmptyShaderFolders(root);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                storage.ReportWarning("Shader files were purged, but empty folders could not be removed: " + root +
                    ". " + error.Message);
            }

            progress?.Report("Central shader folder purged. Choose Apply selection to reinstall packages.");
            return Task.CompletedTask;
        }, cancellationToken);
    }

    private static void RemoveEmptyShaderFolders(string directory)
    {
        LocalFiles.CheckParents(Path.Combine(directory, "check"));
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

    private async Task<(bool Installed, string Message)> UpdatePackageAsync(
        ShaderPackage package,
        IReadOnlyList<ShaderPackage> selection,
        bool rememberSelection,
        IProgress<string>? progress,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        StagingDirectory? staging = null;
        try
        {
            staging = new StagingDirectory(storage.Store);
            ManagerState updatedState = storage.State!.Clone();
            InstalledPackage? installed = updatedState.Packages.SingleOrDefault(item => item.Id == package.Id);
            if (installed != null)
            {
                ShaderFiles.CheckUnmodified(installed, updatedState.Paths);
            }
            progress?.Report("Updating " + package.Name);
            string source = await StageAsync(package, staging.Path, progress, cancellationToken);
            using FileTransaction transaction = storage.BeginTransaction("Update shader package: " + package.Name);
            ShaderFiles.InstallExtracted(package, source, updatedState, transaction, cancellationToken);
            foreach (GameProfile game in updatedState.Games)
            {
                ConfigurationService.Update(updatedState, game, transaction);
            }

            if (rememberSelection)
            {
                updatedState.SelectedPackages = selection.Select(item => item.Id).ToList();
            }

            storage.Commit(updatedState, transaction, cancellationToken);
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
                    staging.Dispose();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    string warning = $"Could not remove temporary files for {package.Name}: {staging.Path}. " +
                        "After this operation finishes, close programs using this folder and remove it " +
                        "to reclaim disk space. " + error.Message;
                    messages.Add(warning);
                    progress?.Report(warning);
                }
            }
        }
    }
    public static ShaderPackage PackageDescriptor(InstalledPackage installed) => new()
    {
        Id = installed.Id,
        Name = installed.Name,
        Repository = installed.Repository,
        DownloadUrl = installed.DownloadUrl
    };

    public ShaderSelectionPlan CreateShaderSelectionPlan(
        IReadOnlyList<ShaderPackage> selected,
        IReadOnlyList<ShaderPackage> catalog)
    {
        ManagerState state = storage.RequireState();
        if (!catalog.Any(package => package.Required))
        {
            throw new InvalidDataException("Refresh the shader catalog before applying a selection; " +
                "required effects are missing.");
        }
        ShaderPackage[] desired = selected.Concat(ShaderDependencies.WithDependencies(selected, catalog, (_, _) => { }))
            .DistinctBy(package => package.Id).OrderBy(package => package.Id, StringComparer.Ordinal).ToArray();
        HashSet<string> ids = desired.Select(package => package.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        InstalledPackage[] removals = state.Clone().Packages.Where(package => !ids.Contains(package.Id))
            .OrderBy(package => package.Id, StringComparer.Ordinal).ToArray();
        string input = state.Paths.Root + JsonSerializer.Serialize(desired, ManagerState.Json) +
            JsonSerializer.Serialize(removals, ManagerState.Json);
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
        return new(desired, removals, fingerprint);
    }

    public Task<PackageUpdateReport> ApplyShaderSelectionAsync(
        ShaderSelectionPlan plan,
        IReadOnlyList<ShaderPackage> catalog,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        bool removalsConfirmed = false)
    {
        if (plan.Removals.Count > 0 && !removalsConfirmed)
        {
            throw new InvalidOperationException("Review and confirm shader package removals " +
                "before applying the selection.");
        }

        return UpdatePackagesAsync(plan.Selected, catalog, progress, cancellationToken, true, plan);
    }

    private void SaveShaderChoices(IReadOnlyList<ShaderPackage> desired, CancellationToken cancellationToken)
    {
        ManagerState updated = storage.State!.Clone();
        updated.SelectedPackages = desired.Select(package => package.Id).ToList();
        using FileTransaction transaction = storage.BeginTransaction("Save shader package selection");
        storage.Commit(updated, transaction, cancellationToken);
    }

    private static IEnumerable<InstalledPackage> OrderShaderRemovals(
        IReadOnlyList<InstalledPackage> removals,
        IReadOnlyList<ShaderPackage> catalog)
    {
        var remaining = removals.ToList();
        while (remaining.Count > 0)
        {
            ShaderPackage[] descriptors = remaining.Select(installed =>
                catalog.SingleOrDefault(package => package.Id == installed.Id) ?? PackageDescriptor(installed))
                .ToArray();
            InstalledPackage next = remaining.FirstOrDefault(candidate => !descriptors.Any(other =>
                ShaderDependencies.Dependencies(other, descriptors).Any(dependency => dependency.Id == candidate.Id))) ??
                throw new InvalidDataException("Shader package dependencies contain a cycle. " +
                    "No further packages were removed.");
            remaining.Remove(next);
            yield return next;
        }
    }

    private (bool Removed, string Message) RemoveShaderPackage(
        InstalledPackage package,
        IReadOnlyList<ShaderPackage> desired,
        IReadOnlyList<ShaderPackage> catalog,
        CancellationToken cancellationToken)
    {
        try
        {
            ShaderPackage[] retained = storage.State!.Packages.Select(installed =>
                catalog.SingleOrDefault(entry => entry.Id == installed.Id) ?? PackageDescriptor(installed)).ToArray();
            string[] consumers = retained.Where(other => ShaderDependencies.Dependencies(other, retained)
                .Any(dependency => dependency.Id == package.Id)).Select(other => other.Name).ToArray();
            if (consumers.Length > 0)
            {
                return (false, $"Kept {package.Name}: still required by {string.Join(", ", consumers)}.");
            }

            ManagerState updated = storage.State!.Clone();
            string[] roots = [ShaderFiles.PackageDirectory(updated.Paths.Shaders, package.Id),
                ShaderFiles.PackageDirectory(updated.Paths.Textures, package.Id)];
            var files = new List<string>();
            foreach (string root in roots)
            {
                LocalFiles.CheckParents(Path.Combine(root, "check"));
                if (LocalFiles.Link(root) != null || File.Exists(root))
                {
                    throw new IOException("A package folder must be a regular directory: " + root);
                }
                if (Directory.Exists(root))
                {
                    foreach (string file in ShaderFiles.EnumerateFiles(root))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        files.Add(file);
                    }
                }
            }

            using (FileTransaction transaction = storage.BeginTransaction("Remove shader package: " + package.Name))
            {
                foreach (string file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    transaction.Delete(file);
                }

                updated.Packages.RemoveAll(entry => entry.Id == package.Id);
                updated.SelectedPackages = desired.Select(entry => entry.Id).ToList();
                foreach (GameProfile game in updated.Games)
                {
                    ConfigurationService.Update(updated, game, transaction, removedSearchRoots: roots);
                }

                storage.Commit(updated, transaction, cancellationToken);
            }

            foreach (string root in roots)
            {
                try
                {
                    RemoveEmptyShaderFolders(root);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    storage.ReportWarning("Package removed; folder could not be cleaned: " + root + ". " + error.Message);
                }
            }
            return (true, "Removed: " + package.Name);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return (false, $"Failed to remove {package.Name}: {UserErrors.Describe(error)}\n" +
                "Package files were retained.");
        }
    }

    public async Task<string> StageAsync(
        ShaderPackage package,
        string staging,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(staging);
        string zip = Path.Combine(staging, "package.zip");
        await downloads.FileAsync(package.DownloadUrl, zip, progress, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        string extracted = Path.Combine(staging, "extracted");
        SafeArchive.Extract(zip, extracted, cancellationToken);
        return extracted;
    }

}
