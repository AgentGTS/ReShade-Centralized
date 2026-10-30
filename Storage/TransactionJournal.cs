namespace ReShadeCentralized;

public sealed class FileSnapshot
{
    public string Path { get; set; } = "";
    public string? Backup { get; set; }

    public string? Hash { get; set; }

    public string? LinkTarget { get; set; }
}

public sealed class TransactionJournal
{
    public string Description { get; set; } = "";
    public string Status { get; set; } = "pending";
    public List<FileSnapshot> Files { get; set; } = [];
    public List<RegistrySnapshot> Registry { get; set; } = [];
}

public sealed class JournalEvent
{
    public FileSnapshot? File { get; set; }

    public RegistrySnapshot? Registry { get; set; }
}
