using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReShadeCentralized;

internal static class StateMigration
{
    public static ManagerState Read(string text, string directory, out IReadOnlyList<string> repairs)
    {
        var changes = new List<string>();
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            parsed = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            changes.Add("Removed JSON comments or trailing commas from storage settings.");
        }

        JsonObject document = parsed as JsonObject ??
            throw new InvalidDataException("Central storage settings must contain a JSON object.");
        int version;
        if (document["SchemaVersion"] is JsonValue value && value.TryGetValue<int>(out int number))
        {
            version = number;
        }
        else if (document["SchemaVersion"] is JsonValue stringValue &&
            stringValue.TryGetValue<string>(out string? versionText) && int.TryParse(versionText, out number))
        {
            version = number;
            changes.Add("Repaired the storage format version.");
        }
        else if (document["SchemaVersion"] == null && document["Paths"] is JsonObject &&
            document["Games"] is JsonArray)
        {
            version = 2;
            changes.Add("Recovered the missing storage format version.");
        }
        else
        {
            throw new InvalidDataException("Cannot identify this storage format. Restore a valid settings file " +
                "or use a fresh setup.");
        }

        if (version > StateStore.CurrentSchemaVersion)
        {
            throw new InvalidDataException("This storage requires a newer app version. Update the app to open it.");
        }

        if (version < 2)
        {
            throw new InvalidDataException("This storage predates the central data layout and requires a fresh setup. "
                + "Unregister games with the previous build before clearing its storage.");
        }

        if (version != StateStore.CurrentSchemaVersion)
        {
            changes.Add($"Migrated central storage format {version} to {StateStore.CurrentSchemaVersion}.");
        }
        document["SchemaVersion"] = StateStore.CurrentSchemaVersion;
        EnsureObject(document, "Paths", changes);
        var pathsNode = (JsonObject)document["Paths"]!;
        string root = Text(pathsNode, "Root");
        if (root.Length == 0 || root == ".")
        {
            pathsNode["Root"] = directory;
            changes.Add("Recovered the central storage path from the selected folder.");
        }
        else if (!Path.IsPathFullyQualified(root) || !LocalFiles.Same(root, directory))
        {
            throw new InvalidDataException("Storage settings refer to a different location: " + root +
                ". Return storage to its original path before opening it; game links still use that path.");
        }

        foreach (string key in new[] { "RuntimeVersions", "RuntimeHashes", "VulkanLayerFiles" })
        {
            EnsureObject(document, key, changes);
        }
        foreach (string key in new[] { "Games", "Packages", "SelectedPackages", "OwnedVulkanApps",
            "OwnedVulkanRegistrations" })
        {
            EnsureArray(document, key, changes);
        }

        CentralPaths paths = CentralPaths.At(directory);
        foreach (JsonNode? entry in (JsonArray)document["Games"]!)
        {
            JsonObject game = entry as JsonObject ?? throw new InvalidDataException("A game record is not an object.");
            NormalizeGame(game, paths, changes);
        }
        foreach (JsonNode? entry in (JsonArray)document["Packages"]!)
        {
            JsonObject package = entry as JsonObject ??
                throw new InvalidDataException("A shader package record is not an object.");
            EnsureObject(package, "Files", changes);
        }

        ManagerState state = document.Deserialize<ManagerState>(ManagerState.Json) ??
            throw new InvalidDataException("Central storage settings are empty.");
        if (version < 4)
        {
            state.SelectedPackages = state.SelectedPackages.Concat(state.Packages.Select(package => package.Id))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            changes.Add("Kept installed shader packages selected for the new selection workflow.");
        }
        if (!Enum.IsDefined(state.VulkanBuild) || state.Games.Any(game =>
            !Enum.IsDefined(game.Api) || !Enum.IsDefined(game.Build) || !Enum.IsDefined(game.Architecture)))
        {
            throw new InvalidDataException("Storage contains an unknown graphics API, runtime build or architecture.");
        }
        int gameCount = state.Games.Count;
        if (state.Games.Select(game => game.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != gameCount ||
            state.Games.Select(game => game.Directory)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != gameCount ||
            state.Games.Select(game => Path.GetDirectoryName(game.Executable))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != gameCount)
        {
            throw new InvalidDataException("Multiple game records use the same profile or game folder. " +
                "Resolve the duplicate settings.");
        }

        repairs = changes;
        return state;
    }

    private static void NormalizeGame(JsonObject game, CentralPaths paths, List<string> changes)
    {
        EnsureArray(game, "Links", changes);
        var links = (JsonArray)game["Links"]!;
        string executable = Text(game, "Executable");
        if (!Path.IsPathFullyQualified(executable))
        {
            string[] folders = links.OfType<JsonObject>().Select(link => Text(link, "Path"))
                .Where(Path.IsPathFullyQualified).Select(path => Path.GetDirectoryName(path)!)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string name = executable.Length > 0 ? executable : Text(game, "Name");
            if (folders.Length == 1 && name.Length > 0 && Path.GetFileName(name) == name &&
                Path.GetExtension(name).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                executable = Path.Combine(folders[0], name);
                game["Executable"] = executable;
                changes.Add("Recovered a game's executable path from its recorded links.");
            }
            else
            {
                throw new InvalidDataException("Cannot recover a game's executable path. " +
                    "Restore its Executable field in ReShadeCentralized.json; existing files have been preserved.");
            }
        }

        string profile = Text(game, "Directory");
        if (profile.Length == 0)
        {
            string[] candidates = links.OfType<JsonObject>()
                .Where(link => Path.GetFileName(Text(link, "Path"))
                    .Equals("ReShade.ini", StringComparison.OrdinalIgnoreCase))
                .Select(link => Text(link, "Target")).Where(Path.IsPathFullyQualified)
                .Select(target => Path.GetDirectoryName(target)!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (candidates.Length > 1)
            {
                throw new InvalidDataException("A game has conflicting central configuration links.");
            }
            profile = candidates.SingleOrDefault() ?? "";
        }
        string id = Text(game, "Id");
        if (id.Length == 0)
        {
            string candidate = Path.GetFileName(profile.TrimEnd(Path.DirectorySeparatorChar)).Split('_')[^1];
            if (!Guid.TryParseExact(candidate, "N", out _))
            {
                throw new InvalidDataException("Cannot recover a game's profile ID from its directory.");
            }
            game["Id"] = id = candidate;
            changes.Add("Recovered a game's ID from its profile folder.");
        }
        LocalFiles.Contained(paths.Screenshots, id);

        if (profile.Length == 0)
        {
            profile = ProfilePaths.DirectoryFor(new ManagerState { Paths = paths },
                new GameProfile { Id = id, Executable = executable });
        }
        if (Text(game, "Directory") != profile)
        {
            game["Directory"] = profile;
            changes.Add("Recovered a game's central profile path.");
        }

        LocalFiles.Contained(paths.Profiles, Path.GetRelativePath(paths.Profiles, profile));
        string displayName = Path.GetFileName(executable);
        if (Text(game, "Name") != displayName)
        {
            game["Name"] = displayName;
            changes.Add("Updated the game label for " + displayName + ".");
        }
        if (File.Exists(executable))
        {
            CpuArchitecture architecture = CompatibilityCatalog.Default.Architecture(executable,
                game["Api"]?.Deserialize<RenderApi>(ManagerState.Json) ?? RenderApi.DXGI);
            if (game["Architecture"]?.ToString() != architecture.ToString())
            {
                game["Architecture"] = architecture.ToString();
                changes.Add("Recovered the executable architecture for " + displayName + ".");
            }
        }

        GameProfile parsed = game.Deserialize<GameProfile>(ManagerState.Json)!;
        if (game["Api"] == null || game["Build"] == null || game["Architecture"] == null ||
            !Enum.IsDefined(parsed.Api) || !Enum.IsDefined(parsed.Build) || !Enum.IsDefined(parsed.Architecture))
        {
            throw new InvalidDataException("Restore the graphics API, build and architecture settings for " +
                displayName + ".");
        }

        var expected = GameFiles.ConfigurationPaths(executable, parsed.Api)
            .Select(path => new OwnedLink(path, Path.Combine(profile, "ReShade.ini"))).ToList();
        if (CompatibilityCatalog.Default.Proxy(executable, parsed.Api) is string proxy)
        {
            string dll = parsed.Architecture == CpuArchitecture.X64 ? "ReShade64.dll" : "ReShade32.dll";
            expected.Add(new(Path.Combine(CompatibilityCatalog.Default.InstallationDirectory(executable, parsed.Api), proxy),
                Path.Combine(paths.Binaries(parsed.Build), dll)));
        }
        var recordedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonNode? entry in links)
        {
            JsonObject recorded = entry as JsonObject ??
                throw new InvalidDataException("A managed link record is invalid.");
            string path = Text(recorded, "Path");
            if (!Path.IsPathFullyQualified(path) || !recordedPaths.Add(Path.GetFullPath(path)))
            {
                throw new InvalidDataException("A managed link has an invalid or duplicate file path.");
            }

            string target = Text(recorded, "Target");
            if (target.Length == 0 &&
                expected.SingleOrDefault(link => LocalFiles.Same(link.Path, path)) is { } known)
            {
                recorded["Target"] = target = known.Target;
                changes.Add("Recovered the managed target for " + path + ".");
            }
            if (!Path.IsPathFullyQualified(target))
            {
                throw new InvalidDataException("A managed link target cannot be recovered: " + path);
            }
        }
        foreach (OwnedLink link in expected)
        {
            if (!recordedPaths.Contains(link.Path))
            {
                links.Add(JsonSerializer.SerializeToNode(link, ManagerState.Json));
                changes.Add("Recovered the managed link record for " + link.Path + ".");
            }
        }
    }

    private static string Text(JsonObject document, string key)
    {
        if (document[key] == null)
        {
            return "";
        }
        return document[key] is JsonValue value && value.TryGetValue<string>(out string? text) ? text ?? "" :
            throw new InvalidDataException("Storage setting " + key + " must be text.");
    }

    private static void EnsureArray(JsonObject document, string key, List<string> changes)
    {
        if (document[key] == null)
        {
            document[key] = new JsonArray();
            changes.Add("Restored the missing " + key + " collection.");
        }
        else if (document[key] is not JsonArray)
        {
            throw new InvalidDataException("Storage setting " + key + " must be a list.");
        }
    }

    private static void EnsureObject(JsonObject document, string key, List<string> changes)
    {
        if (document[key] == null)
        {
            document[key] = new JsonObject();
            changes.Add("Restored the missing " + key + " settings.");
        }
        else if (document[key] is not JsonObject)
        {
            throw new InvalidDataException("Storage setting " + key + " must be an object.");
        }
    }
}
