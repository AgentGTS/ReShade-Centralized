namespace ReShadeCentralized;

public sealed record ExistingInstallationNotice(string Message, bool BlocksInstallation);
public sealed record ExistingFile(string? LinkTarget);
public sealed record InstallationLink(string Path, string Target, ExistingFile? Previous)
{
    public bool ReplacesFile => Previous != null;
    public string? PreviousTarget => Previous?.LinkTarget;
}

public sealed record InstallationPlan(
    string GameDirectory,
    string ProfileDirectory,
    IReadOnlyList<InstallationLink> Links,
    IReadOnlyList<string> RemovedLinks,
    IReadOnlyList<string> LocalSearchPaths,
    string? PresetSource,
    string Fingerprint,
    string? Conflict)
{
    public string? Executable { get; init; }
    public string? TargetReason { get; init; }
    public string? ConfigurationSource { get; init; }
    public CpuArchitecture? RuntimeArchitecture { get; init; }

    public bool RequiresConfirmation => Links.Any(link => link.ReplacesFile);

    public string Describe(bool includePaths = true)
    {
        string FileLabel(string path) => includePaths ? path :
            Executable != null && path.StartsWith(Path.GetDirectoryName(Executable)! + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(Path.GetDirectoryName(Executable)!, path) :
            Path.GetFileName(path);
        var lines = new List<string>();
        if (TargetReason != null)
        {
            lines.Add($"{TargetReason}: {Executable}");
        }
        if (Executable != null && !LocalFiles.Same(GameDirectory, Path.GetDirectoryName(Executable)!))
        {
            lines.Add("Install folder: " + GameDirectory);
        }
        if (RuntimeArchitecture != null)
        {
            lines.Add("Runtime architecture: " + (RuntimeArchitecture == CpuArchitecture.X64 ? "64-bit" : "32-bit"));
        }
        foreach (InstallationLink link in Links)
        {
            string operation = "Link";
            if (link.ReplacesFile)
            {
                operation = link.PreviousTarget == null ? "Replace file" : "Replace link";
            }
            if (includePaths)
            {
                string previous = link.PreviousTarget == null ? "" : $"\n  Current target: {link.PreviousTarget}";
                lines.Add($"{operation}: {link.Path}{previous}\n  -> {link.Target}");
            }
            else
            {
                string destination = LocalFiles.Same(link.Target, Path.Combine(ProfileDirectory, "ReShade.ini"))
                    ? "game profile" : "central runtime";
                lines.Add($"{operation}: {FileLabel(link.Path)} -> {destination} ({FileLabel(link.Target)}).");
            }
        }

        lines.AddRange(RemovedLinks.Select(path => "Remove managed link: " + FileLabel(path)));
        if (PresetSource != null)
        {
            lines.Add(includePaths
                ? $"Copy preset: {PresetSource}\n  -> {Path.Combine(ProfileDirectory, "ReShadePreset.ini")}"
                : $"Copy preset: {FileLabel(PresetSource)} -> game profile (ReShadePreset.ini).");
        }

        lines.AddRange(LocalSearchPaths.Select(path => "Keep local shaders/textures: " +
            (includePaths ? path : "reshade-shaders\\" + Path.GetFileName(path.TrimEnd('*', '\\', '/')))));
        if (RequiresConfirmation)
        {
            lines.Add("Replaced files will be deleted. Files that existing links point to are kept. " +
                "Unregister will not recover deleted files.");
        }

        if (Conflict != null)
        {
            string conflict = Conflict;
            if (!includePaths)
            {
                string[] paths = GameFiles.ProxyNames.Append("ReShade.ini")
                    .Select(name => Path.Combine(GameDirectory, name))
                    .Concat(Links.Select(link => link.Path)).Concat(RemovedLinks)
                    .Distinct().OrderByDescending(path => path.Length).ToArray();
                foreach (string path in paths)
                {
                    conflict = conflict.Replace(path, FileLabel(path), StringComparison.OrdinalIgnoreCase);
                }
            }
            lines.Add(conflict);
        }

        return string.Join("\n\n", lines);
    }
}
