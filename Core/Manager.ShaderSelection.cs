using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ReShadeCentralized;

public sealed partial class Manager
{
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
        ManagerState state = State ?? throw new InvalidOperationException("Choose central storage in Setup first.");
        if (!catalog.Any(package => package.Required))
        {
            throw new InvalidDataException("Refresh the shader catalog before applying a selection; " +
                "required effects are missing.");
        }
        ShaderPackage[] desired = selected.Concat(ShaderService.WithDependencies(selected, catalog, (_, _) => { }))
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

        return RunPackageBatchAsync(plan.Selected, catalog, progress, cancellationToken, true, plan);
    }

    private void SaveShaderChoices(IReadOnlyList<ShaderPackage> desired, CancellationToken cancellationToken)
    {
        ManagerState updated = State!.Clone();
        updated.SelectedPackages = desired.Select(package => package.Id).ToList();
        using FileTransaction transaction = BeginTransaction("Save shader package selection");
        cancellationToken.ThrowIfCancellationRequested();
        Store.Save(updated, transaction);
        transaction.Commit();
        State = updated;
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
                ShaderService.Dependencies(other, descriptors).Any(dependency => dependency.Id == candidate.Id))) ??
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
            ShaderPackage[] retained = State!.Packages.Select(installed =>
                catalog.SingleOrDefault(entry => entry.Id == installed.Id) ?? PackageDescriptor(installed)).ToArray();
            string[] consumers = retained.Where(other => ShaderService.Dependencies(other, retained)
                .Any(dependency => dependency.Id == package.Id)).Select(other => other.Name).ToArray();
            if (consumers.Length > 0)
            {
                return (false, $"Kept {package.Name}: still required by {string.Join(", ", consumers)}.");
            }

            ManagerState updated = State.Clone();
            string[] roots = [ShaderService.PackageDirectory(updated.Paths.Shaders, package.Id),
                ShaderService.PackageDirectory(updated.Paths.Textures, package.Id)];
            var files = new List<string>();
            foreach (string root in roots)
            {
                FileTransaction.CheckParents(Path.Combine(root, "check"));
                if (FileTransaction.Link(root) != null || File.Exists(root))
                {
                    throw new IOException("A package folder must be a regular directory: " + root);
                }
                if (Directory.Exists(root))
                {
                    foreach (string file in ShaderService.EnumerateFiles(root))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        files.Add(file);
                    }
                }
            }

            using (FileTransaction transaction = BeginTransaction("Remove shader package: " + package.Name))
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

                cancellationToken.ThrowIfCancellationRequested();
                Store.Save(updated, transaction);
                transaction.Commit();
                State = updated;
            }

            foreach (string root in roots)
            {
                try
                {
                    RemoveEmptyShaderFolders(root);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    ReportWarning("Package removed; folder could not be cleaned: " + root + ". " + error.Message);
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
}
