using System.Text.Json;

namespace ReShadeCentralized;

public sealed class StateStore
{
    public const int CurrentSchemaVersion = 4;

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ReShade Centralized");

    private readonly string? applicationDirectory;
    private string? selectedDirectory;
    private string? loadedStateHash;

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
        loadedStateHash = null;

        LocalFiles.CheckParents(PointerPath);
        CheckRegularFile(PointerPath);
        if (LocalFiles.Link(PointerPath) != null)
        {
            throw new IOException("The central storage pointer must not be a link: " + PointerPath);
        }

        if (!File.Exists(PointerPath))
        {
            return;
        }

        using var stream = new FileStream(PointerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 64 * 1024)
        {
            throw new InvalidDataException("The central storage pointer is too large: " + PointerPath);
        }
        byte[] contents = new byte[checked((int)stream.Length)];
        stream.ReadExactly(contents);
        SelectDirectory(ConfigurationService.ReadConfigurationText(contents).Trim());

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
            loadedStateHash = null;
            return;
        }

        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
        {
            throw new InvalidDataException("Select an absolute central storage folder path.");
        }

        if (directory.StartsWith(@"\\?\", StringComparison.Ordinal)
            || directory.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Device paths cannot be used for central storage.");
        }
        string suppliedRoot = Path.GetPathRoot(directory)!;
        LocalFiles.Contained(suppliedRoot, directory[suppliedRoot.Length..]);
        string path = Path.GetFullPath(directory);
        string filesystemRoot = Path.GetPathRoot(path)!;
        if (LocalFiles.Same(path, filesystemRoot))
        {
            throw new InvalidDataException("Choose a central storage folder below the drive or share root.");
        }
        LocalFiles.Contained(filesystemRoot, Path.GetRelativePath(filesystemRoot, path));

        if (applicationDirectory != null
            && (LocalFiles.Same(path, applicationDirectory)
                || path.StartsWith(
                    applicationDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Choose central storage outside the app folder so deleting the app " +
                "keeps game settings and ReShade files.");
        }

        LocalFiles.CheckParents(Path.Combine(path, "ReShadeCentralized.json"));
        selectedDirectory = path;
        loadedStateHash = null;
    }

    internal void RememberSelection()
    {
        if (PointerPath == null)
        {
            return;
        }

        LocalFiles.CheckParents(PointerPath);
        CheckRegularFile(PointerPath);

        if (LocalFiles.Link(PointerPath) != null)
        {
            throw new IOException("The central storage pointer must not be a link: " + PointerPath);
        }

        Directory.CreateDirectory(applicationDirectory!);
        string temporary = PointerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(DirectoryPath);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            LocalFiles.CheckParents(PointerPath);
            CheckRegularFile(PointerPath);
            if (LocalFiles.Link(PointerPath) != null)
            {
                throw new IOException("The central storage pointer must not be a link: " + PointerPath);
            }
            LocalFiles.AtomicMove(temporary, PointerPath);
        }
        finally
        {
            LocalFiles.CheckParents(temporary);
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
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
            throw new InvalidDataException(error.Message + "\n\nExisting files were kept at:\n" + DirectoryPath +
                "\nBack up this folder, including Recovery-v2, before repairing its settings. " +
                "Restore a valid ReShadeCentralized.json from your backup or choose a new empty folder " +
                "for a fresh setup. Keep the existing storage while registered games still use it.", error);
        }
    }

    private ManagerState? LoadState(out IReadOnlyList<string> repairs)
    {
        repairs = [];

        if (!IsSelected || !LocalFiles.Exists(StatePath))
        {
            loadedStateHash = null;
            return null;
        }

        if (LocalFiles.Link(StatePath) != null)
        {
            throw new InvalidDataException("Central storage state must not be a link: " + StatePath);
        }

        CheckRegularFile(StatePath);

        using var stream = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 64 * 1024 * 1024)
        {
            throw new InvalidDataException("Central storage settings exceed the 64 MB safety limit.");
        }
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        ManagerState state = StateMigration.Read(
            ConfigurationService.ReadConfigurationText(bytes), DirectoryPath, out repairs);

        if (state.VulkanLayerDirectory != null
            && !LocalFiles.Same(state.VulkanLayerDirectory, Path.Combine(DirectoryPath, "Vulkan")))
        {
            throw new InvalidDataException("This beta requires a fresh setup for Vulkan files stored outside " +
                "central storage. Unregister games with the previous build before setting up again.");
        }

        loadedStateHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        return state;
    }

    public string Save(
        ManagerState state,
        FileTransaction transaction)
    {
        LocalFiles.CheckParents(StatePath);
        CheckRegularFile(StatePath);
        if (LocalFiles.Link(StatePath) != null)
        {
            throw new IOException("Central storage state must not be a link: " + StatePath);
        }
        string text = JsonSerializer.Serialize(state, ManagerState.Json);
        if (!transaction.WriteTextIfUnchanged(StatePath, loadedStateHash, text))
        {
            throw new IOException("Central storage settings changed outside the app. They were preserved. " +
                "Back up the settings and restart the app before retrying: " + StatePath);
        }
        return LocalFiles.HashText(text);
    }

    internal void AcceptSavedState(string hash) => loadedStateHash = hash;

    private static void CheckRegularFile(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new IOException("A storage settings or pointer file must be a regular file: " + path);
            }
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
        }
    }
}
