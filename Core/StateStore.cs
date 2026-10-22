using System.Text.Json;

namespace ReShadeCentralized;

public sealed class StateStore(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public string StatePath => Path.Combine(DirectoryPath, "ReShadeCentralized.json");
    public string Backups => Path.Combine(DirectoryPath, "Backups");
    public string Staging => Path.Combine(DirectoryPath, "Staging");

    public ManagerState? Load()
    {
        if (!File.Exists(StatePath))
        {
            return null;
        }

        ManagerState state = JsonSerializer.Deserialize<ManagerState>(File.ReadAllText(StatePath), ManagerState.Json) ??
            throw new InvalidDataException("Application state is empty.");
        if (state.SchemaVersion != 1)
        {
            throw new InvalidDataException("Unsupported application state version. Use a compatible manager.");
        }

        foreach (string path in state.Paths.Directories())
        {
            if (!Path.IsPathFullyQualified(path))
            {
                throw new InvalidDataException("State contains a non-absolute storage path.");
            }
        }

        return state;
    }

    public void Save(
        ManagerState state,
        FileTransaction transaction) =>
        transaction.WriteText(
        StatePath,
        JsonSerializer.Serialize(state, ManagerState.Json));
}
