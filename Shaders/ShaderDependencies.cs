namespace ReShadeCentralized;

public static class ShaderDependencies
{
    public static IReadOnlyList<ShaderPackage> WithDependencies(
        IEnumerable<ShaderPackage> selected,
        IReadOnlyList<ShaderPackage> catalog,
        Action<ShaderPackage, string>? dependencyUnavailable = null)
    {
        var result = selected.Concat(catalog.Where(package => package.Required)).DistinctBy(package => package.Id)
            .ToList();
        // The upstream catalog describes this dependency but has no structured dependency field.
        ShaderPackage? dependent = result.SingleOrDefault(package =>
            ShaderCatalog.CanonicalRepository(package.Repository).EndsWith("/yplebedev/bfbfx"));
        if (dependent != null)
        {
            ShaderPackage? framework = catalog.SingleOrDefault(package =>
                ShaderCatalog.CanonicalRepository(package.Repository).EndsWith("/zenteon/zenteonfx"));
            if (framework == null)
            {
                const string message = "BFBFX requires ZenteonFX, which is missing from the catalog. " +
                    "Open Shader packages > Refresh catalog, then apply the selection again.";
                if (dependencyUnavailable == null)
                {
                    throw new InvalidDataException(message);
                }

                result.Remove(dependent);
                dependencyUnavailable(dependent, message);
            }
            else if (!result.Any(package => package.Id == framework.Id))
            {
                result.Add(framework);
            }
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
        IReadOnlyList<ShaderPackage> selection)
    {
        bool needsFramework = ShaderCatalog.CanonicalRepository(package.Repository).EndsWith("/yplebedev/bfbfx");
        return selection.Where(candidate => candidate.Id != package.Id &&
            ((!package.Required && candidate.Required) ||
            (needsFramework && ShaderCatalog.CanonicalRepository(candidate.Repository).EndsWith("/zenteon/zenteonfx"))));
    }

}
