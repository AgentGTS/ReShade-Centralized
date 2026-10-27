using System.Text.Json;

namespace ReShadeCentralized.Tests;

internal static partial class TestSuite
{
    private static async Task FreshBetaSetupIgnoresOldAssetsAsync()
    {
        string directory = CreateTestDirectory("fresh-beta-setup");
        var store = new StateStore(Path.Combine(directory, "manager"));
        Directory.CreateDirectory(store.DirectoryPath);
        foreach (string folder in new[] { "Backups", "Recovery" })
        {
            string archive = Path.Combine(store.DirectoryPath, folder, "old-operation");
            Directory.CreateDirectory(archive);
            File.WriteAllText(Path.Combine(archive, "transaction.json"), "not a current recovery journal");
        }

        string oldSettings = Path.Combine(store.DirectoryPath, "ReShadeCentralized.ini");
        string oldTemplate = Path.Combine(store.DirectoryPath, "ReShade_Template.ini");
        File.WriteAllText(oldSettings, "old settings");
        File.WriteAllText(oldTemplate, "old template");
        string central = Path.Combine(directory, "central");
        string unused = Path.Combine(central, "presets", "old-profile", "ReShade.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(unused)!);
        File.WriteAllText(unused, "old profile");
        using var manager = new Manager(store, new InMemoryLayerRegistry());
        manager.Open();
        AssertTrue(manager.State == null);
        await manager.InitializeAsync(central, None);
        AssertTrue(manager.State!.Games.Count == 0 && manager.State.Paths.Root == central);
        AssertTrue(File.ReadAllText(oldSettings) == "old settings" && File.ReadAllText(oldTemplate) == "old template");
        AssertTrue(File.ReadAllText(unused) == "old profile");
        AssertTrue(!Directory.Exists(Path.Combine(manager.State.Paths.Shaders, "development")));
        using var json = JsonDocument.Parse(File.ReadAllText(store.StatePath));
        AssertTrue(json.RootElement.GetProperty("Paths").EnumerateObject().Single().Name == "Root");
        AssertTrue(store.Load()!.SchemaVersion == StateStore.CurrentSchemaVersion);
    }

    private static Task IncompatibleBetaStateRequiresFreshSetupAsync()
    {
        foreach (string version in new[] { "1", "99", "\"invalid\"", "null", "missing" })
        {
            string directory = CreateTestDirectory("incompatible-beta-state");
            var store = new StateStore(directory);
            string original = version == "missing" ? "{}" : "{\"SchemaVersion\":" + version + "}";
            File.WriteAllText(store.StatePath, original);
            using var manager = new Manager(store, new InMemoryLayerRegistry());
            try
            {
                manager.Open();
                throw new Exception("An incompatible beta state was accepted.");
            }
            catch (InvalidDataException error)
            {
                AssertTrue(error.Message.Contains(version == "99" ? "newer app version" : "fresh setup"));
            }

            AssertTrue(File.ReadAllText(store.StatePath) == original && manager.State == null);
        }

        return Task.CompletedTask;
    }

    private static Task CurrentBetaStateRequiresCompleteProfilesAsync()
    {
        string directory = CreateTestDirectory("complete-beta-profiles");
        var store = new StateStore(directory);
        var state = new ManagerState { Paths = CentralPaths.At(directory) };
        var game = new GameProfile { Directory = Path.Combine(state.Paths.Profiles, "profile") };
        state.Games.Add(game);
        File.WriteAllText(store.StatePath, JsonSerializer.Serialize(state, ManagerState.Json));
        AssertThrows<InvalidDataException>(() => store.Load());
        game.Executable = Path.Combine(directory, "game.exe");
        File.WriteAllText(store.StatePath, JsonSerializer.Serialize(state, ManagerState.Json));
        AssertTrue(store.Load()!.Games.Single().Links.Count == 2);
        game.Links.Add(new(Path.Combine(directory, "ReShade.ini"), ConfigurationService.ConfigPath(game)));
        File.WriteAllText(store.StatePath, JsonSerializer.Serialize(state, ManagerState.Json));
        AssertTrue(store.Load()!.Games.Single().Executable == game.Executable);
        return Task.CompletedTask;
    }
}
