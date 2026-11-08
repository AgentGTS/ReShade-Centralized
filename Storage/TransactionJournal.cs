namespace ReShadeCentralized;

public sealed class FileSnapshot
{
    public string Path { get; set; } = "";
    public string? Backup { get; set; }

    public string? Hash { get; set; }

    public string? LinkTarget { get; set; }

    public bool RestoreOnlyIfMissing { get; set; }

    public string? ExpectedModifiedHash { get; set; }
}

public sealed class TransactionJournal
{
    public string Description { get; set; } = "";
    public string Status { get; set; } = "pending";
    public List<FileSnapshot> Files { get; set; } = [];
    public List<FileMutation> Mutations { get; set; } = [];
    public List<RegistrySnapshot> Registry { get; set; } = [];
    public List<RegistryMutation> RegistryMutations { get; set; } = [];
}

public enum FileMutationKind
{
    Missing,
    Contents,
    SymbolicLink
}

public sealed class FileMutation
{
    public string Path { get; set; } = "";
    public FileMutationKind Kind { get; set; }
    public string? Value { get; set; }
}

public sealed record RegistryMutation(LayerRegistration Registration, int? Value);

public sealed class JournalEvent
{
    public FileSnapshot? File { get; set; }

    public RegistrySnapshot? Registry { get; set; }

    public FileMutation? Mutation { get; set; }

    public RegistryMutation? RegistryMutation { get; set; }
}
