using System.Text.Json;

namespace ReShadeCentralized;

public sealed class StateStore
{
    public const int CurrentSchemaVersion = 4;
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ReShade Centralized");

    private readonly string? applicationDirectory;
    private string? selectedDirectory;

    public StateStore(string directory) : this(directory, applicationFolder: false)
    {
    }

    private StateStore(string directory, bool applicationFolder)
    {
        if (applicationFolder)
        {
            applicationDirectory = Path.GetFullPath(directory);
        }
        else
        {
            SelectDirectory(directory);
        }
    }

    public static StateStore ForApplication(string directory) => new(directory, applicationFolder: true);

    public bool IsSelected => selectedDirectory != null;
    public string DirectoryPath => selectedDirectory ??
        throw new InvalidOperationException("Choose a central storage folder first.");
    public string? PointerPath => applicationDirectory == null ? null :
        Path.Combine(applicationDirectory, "CentralStorage.txt");
    public string StatePath => Path.Combine(DirectoryPath, "ReShadeCentralized.json");
    public string Recovery => Path.Combine(DirectoryPath, "Recovery-v2");
    public string Staging => Path.Combine(DirectoryPath, "Staging");

    internal void ReadSelection()
    {
        if (PointerPath == null)
        {
            return;
        }

        selectedDirectory = null;
        if (!File.Exists(PointerPath))
        {
            return;
        }

        SelectDirectory(File.ReadAllText(PointerPath).Trim());
        if (!Directory.Exists(DirectoryPath))
        {
            throw new IOException("Saved central storage is unavailable: " +
                DirectoryPath + ". Reconnect its drive or choose a folder in Setup.");
        }
    }

    internal void SelectDirectory(string? directory)
    {
        if (directory == null)
        {
            selectedDirectory = null;
            return;
        }

        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
        {
            throw new InvalidDataException("Select an absolute central storage folder path.");
        }

        string path = Path.GetFullPath(directory);
        if (applicationDirectory != null && (LocalFiles.Same(path, applicationDirectory) ||
            path.StartsWith(applicationDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Choose central storage outside the app folder so deleting the app " +
                "keeps game settings and ReShade files.");
        }

        LocalFiles.CheckParents(Path.Combine(path, "ReShadeCentralized.json"));
        selectedDirectory = path;
    }

    internal void RememberSelection()
    {
        if (PointerPath == null)
        {
            return;
        }

        LocalFiles.CheckParents(PointerPath);
        if (LocalFiles.Link(PointerPath) != null || LocalFiles.Link(PointerPath + ".tmp") != null)
        {
            throw new IOException("The central storage pointer must not be a link: " + PointerPath);
        }

        Directory.CreateDirectory(applicationDirectory!);
        File.WriteAllText(PointerPath + ".tmp", DirectoryPath);
        File.Move(PointerPath + ".tmp", PointerPath, true);
    }

    public ManagerState? Load() => Load(out _);

    internal ManagerState? Load(out IReadOnlyList<string> repairs)
    {
        repairs = [];
        try
        {
            return LoadState(out repairs);
        }
        catch (Exception error) when (error is InvalidDataException or JsonException)
        {
            throw new InvalidDataException(error.Message + "\n\nTo reuse the default location for a fresh setup, " +
                "unregister affected games first, then delete the central storage folder at:\n" + DefaultDirectory +
                "\nSelect this path in Setup; it will be recreated. Deleting storage removes its settings, " +
                "presets, shaders and runtimes.", error);
        }
    }

    private ManagerState? LoadState(out IReadOnlyList<string> repairs)
    {
        repairs = [];
        if (!IsSelected || !LocalFiles.Exists(StatePath))
        {
            return null;
        }

        if (LocalFiles.Link(StatePath) != null)
        {
            throw new InvalidDataException("Central storage state must not be a link: " + StatePath);
        }

        ManagerState state = StateMigration.Read(File.ReadAllText(StatePath), DirectoryPath, out repairs);
        if (state.VulkanLayerDirectory != null &&
            !LocalFiles.Same(state.VulkanLayerDirectory, Path.Combine(DirectoryPath, "Vulkan")))
        {
            throw new InvalidDataException("This beta requires a fresh setup for Vulkan files stored outside " +
                "central storage. Unregister games with the previous build before setting up again.");
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
