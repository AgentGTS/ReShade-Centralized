using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReShadeCentralized.Tests;

internal static partial class TestSuite
{
    private static async Task CentralStateMigrationRepairsRecoverableSettingsAsync()
    {
        string directory = CreateTestDirectory("central-state-migration");
        var store = new StateStore(Path.Combine(directory, "central"));
        using (var manager = new Manager(store, new InMemoryLayerRegistry()))
        {
            await manager.InitializeAsync(store.DirectoryPath, None);
        }

        ManagerState state = store.Load()!;
        GameProfile game = CreateRegistrationGame(state, Path.Combine(directory, "game"));
        state.Games.Add(game);
        string profile = game.Directory;
        Directory.CreateDirectory(profile);
        string preset = Path.Combine(profile, "ReShadePreset.ini");
        File.WriteAllText(preset, "Keep this preset");
        string foreignHook = Path.Combine(Path.GetDirectoryName(game.Executable)!, "dxgi.dll");
        File.WriteAllText(foreignHook, "Unrelated hook - preserve this file");
        JsonObject original = JsonSerializer.SerializeToNode(state, ManagerState.Json)!.AsObject();
        original["SchemaVersion"] = 2;
        original["Paths"]!["Root"] = null;
        original["RuntimeVersions"] = null;
        original["Packages"] = null;
        original["SelectedPackages"] = null;
        original["Games"]![0]!["Name"] = null;
        original["Games"]![0]!["Architecture"] = "X86";
        original["Games"]![0]!["Links"] = null;
        File.WriteAllText(store.StatePath, original.ToJsonString());
        var notices = new List<string>();
        using var reopened = new Manager(store, new InMemoryLayerRegistry());
        reopened.Warning += notices.Add;
        reopened.Open();
        AssertTrue(reopened.State!.SchemaVersion == StateStore.CurrentSchemaVersion);
        GameProfile recovered = reopened.State.Games.Single();
        AssertTrue(recovered.Id == game.Id && recovered.Directory == profile && recovered.Name == "MyGame.exe");
        AssertTrue(recovered.Architecture == CpuArchitecture.X64 && recovered.Links.Count == 2);
        AssertTrue(reopened.State.Paths.Root == store.DirectoryPath && reopened.State.Packages.Count == 0);
        AssertTrue(File.ReadAllText(preset) == "Keep this preset" &&
            File.ReadAllText(foreignHook) == "Unrelated hook - preserve this file");
        AssertTrue(notices.Any(notice => notice.Contains("Migrated central storage")));
        AssertTrue(DeploymentService.GetInstallationWarning(recovered) != null);
        string committed = File.ReadAllText(store.StatePath);
        notices.Clear();
        reopened.Open();
        AssertTrue(notices.Count == 0 && File.ReadAllText(store.StatePath) == committed);
        AssertTrue(!Directory.EnumerateFileSystemEntries(store.Recovery).Any());
        UnregisterResult removed = await reopened.UnregisterAsync(game.Id, None);
        AssertTrue(removed.PreservedFiles.Contains(foreignHook) && File.ReadAllText(preset) == "Keep this preset");
    }

    private static Task CentralStateMigrationRecoversProfileIdentityAsync()
    {
        foreach (bool missingVersion in new[] { false, true })
        {
            string directory = CreateTestDirectory("recover-profile-identity");
            var store = new StateStore(directory);
            ManagerState state = CreateState(directory);
            GameProfile game = CreateRegistrationGame(state, Path.Combine(directory, "game"));
            game.Name = Path.GetFileName(game.Executable);
            game.Links.Add(new(Path.Combine(Path.GetDirectoryName(game.Executable)!, "ReShade.ini"),
                ConfigurationService.ConfigPath(game)));
            state.Games.Add(game);
            JsonObject document = JsonSerializer.SerializeToNode(state, ManagerState.Json)!.AsObject();
            document["SchemaVersion"] = missingVersion ? null : JsonValue.Create("2");
            JsonNode record = document["Games"]![0]!;
            record["Id"] = null;
            record["Directory"] = null;
            record["Executable"] = null;
            string text = document.ToJsonString();
            text = "/* recoverable settings */\n" + text[..^1] + ",}";
            File.WriteAllText(store.StatePath, text);
            using var manager = new Manager(store, new InMemoryLayerRegistry());
            manager.Open();
            GameProfile recovered = manager.State!.Games.Single();
            AssertTrue(recovered.Id == game.Id && recovered.Directory == game.Directory &&
                recovered.Executable == game.Executable && recovered.Links.Count == 2);
            using var saved = JsonDocument.Parse(File.ReadAllText(store.StatePath));
            AssertTrue(saved.RootElement.GetProperty("SchemaVersion").GetInt32() == StateStore.CurrentSchemaVersion);
        }

        return Task.CompletedTask;
    }

    private static Task CentralStateMigrationPreservesUnrecoverableDataAsync()
    {
        string[] variants = ["truncated", "future", "invalid-api", "duplicate", "invalid-link", "wrong-type"];
        foreach (string variant in variants)
        {
            string directory = CreateTestDirectory("unrecoverable-state");
            var store = new StateStore(directory);
            ManagerState state = CreateState(directory);
            GameProfile game = CreateRegistrationGame(state, Path.Combine(directory, "game"));
            state.Games.Add(game);
            JsonObject document = JsonSerializer.SerializeToNode(state, ManagerState.Json)!.AsObject();
            document["SchemaVersion"] = 2;
            switch (variant)
            {
                case "future":
                    document["SchemaVersion"] = StateStore.CurrentSchemaVersion + 1;
                    break;
                case "invalid-api":
                    document["Games"]![0]!["Api"] = "UnknownApi";
                    break;
                case "duplicate":
                    document["Games"]!.AsArray().Add(document["Games"]![0]!.DeepClone());
                    break;
                case "invalid-link":
                    document["Games"]![0]!["Links"] = new JsonArray((JsonNode?)null);
                    break;
                case "wrong-type":
                    document["Games"] = "not a list";
                    break;
            }

            string original = document.ToJsonString();
            if (variant == "truncated")
            {
                original = original[..^4];
            }
            File.WriteAllText(store.StatePath, original);
            using var manager = new Manager(store, new InMemoryLayerRegistry());
            AssertThrows<InvalidDataException>(manager.Open);
            AssertTrue(manager.State == null && File.ReadAllText(store.StatePath) == original);
            AssertTrue(!Directory.Exists(store.Recovery));
        }

        return Task.CompletedTask;
    }

    private static Task CentralStateMigrationSaveFailureAndRecoveryAsync()
    {
        string directory = CreateTestDirectory("migration-rollback");
        var store = new StateStore(directory);
        ManagerState state = CreateState(directory);
        state.SchemaVersion = 2;
        string original = JsonSerializer.Serialize(state, ManagerState.Json);
        File.WriteAllText(store.StatePath, original);
        using var manager = new Manager(store, new InMemoryLayerRegistry());
        using (var locked = new FileStream(store.StatePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            AssertThrows<IOException>(manager.Open);
            AssertTrue(manager.State == null && File.ReadAllText(store.StatePath) == original);
        }

        var interrupted = new FileTransaction(store.Recovery, "Interrupted storage migration");
        interrupted.WriteText(store.StatePath, "incomplete migration");
        manager.Open();
        AssertTrue(manager.State!.SchemaVersion == StateStore.CurrentSchemaVersion);
        AssertTrue(store.Load()!.SchemaVersion == StateStore.CurrentSchemaVersion);
        AssertTrue(!Directory.EnumerateFileSystemEntries(store.Recovery).Any());
        return Task.CompletedTask;
    }
}
