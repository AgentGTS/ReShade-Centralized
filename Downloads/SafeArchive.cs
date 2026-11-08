using System.IO.Compression;
using System.Text.RegularExpressions;

namespace ReShadeCentralized;

public static class SafeArchive
{
    public const long MaximumInstallerBytes = 512L * 1024 * 1024;
    private const long MaximumExpandedBytes = 16L * 1024 * 1024 * 1024;
    private const long MaximumInstallerEntryBytes = 128L * 1024 * 1024;

    public static void Extract(
        string archivePath,
        string destination,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        Extract(archive, destination, cancellationToken);
    }

    public static void Extract(
        ZipArchive archive,
        string destination,
        CancellationToken cancellationToken = default)
    {
        long expanded = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Validate the entire archive before writing anything.
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = entry.FullName.Replace('\\', '/');
            if (name.Split('/').Any(value => value == ".."
                || value.Contains(':')
                || value.TrimEnd(' ', '.') != value
                || Regex.IsMatch(value, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", RegexOptions.IgnoreCase)))
            {
                throw new InvalidDataException("Unsafe archive entry: " + name);
            }

            string target = LocalFiles.Contained(destination, name.Replace('/', Path.DirectorySeparatorChar));
            if (!paths.Add(target))
            {
                throw new InvalidDataException("Duplicate archive entry: " + name);
            }

            int mode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (mode == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Archive links are not supported.");
            }

            expanded = checked(expanded + entry.Length);
            if (expanded > MaximumExpandedBytes || archive.Entries.Count > 100000)
            {
                throw new InvalidDataException("Archive exceeds extraction limits.");
            }

            LocalFiles.CheckParents(target);
            if (LocalFiles.Exists(target))
            {
                throw new IOException("Archive extraction requires a fresh directory: " + target);
            }
        }

        expanded = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string target = LocalFiles.Contained(
                destination,
                entry.FullName.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
            if (entry.Name.Length == 0)
            {
                LocalFiles.CheckParents(Path.Combine(target, "check"));
                Directory.CreateDirectory(target);
            }
            else
            {
                LocalFiles.CheckParents(target);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                CopyEntry(entry, target, MaximumExpandedBytes, ref expanded, cancellationToken);
            }
        }
    }

    public static void ExtractInstaller(
        string installer,
        string destination,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using FileStream input = File.OpenRead(installer);
        if (input.Length > MaximumInstallerBytes)
        {
            throw new InvalidDataException("The installer exceeds the 512 MB size limit.");
        }

        byte[] block = new byte[512];
        while (input.Length - input.Position >= block.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            input.ReadExactly(block);
            if (block[0] != 0x50 || block[1] != 0x4B || block[2] != 3 || block[3] != 4
                || !block.Skip(4).Take(26).Any(item => item != 0))
            {
                continue;
            }

            using var payload = new PayloadStream(input, input.Position - block.Length);
            payload.Position = 0;
            using var zip = new ZipArchive(payload, ZipArchiveMode.Read);
            if (zip.Entries.Count > 100000)
            {
                throw new InvalidDataException("Installer archive contains too many entries.");
            }

            foreach (string file in RuntimeService.PayloadFiles)
            {
                ZipArchiveEntry[] entries = zip.Entries.Where(item => item.FullName == file).ToArray();
                if (entries.Length != 1 || entries[0].Length > MaximumInstallerEntryBytes)
                {
                    throw new InvalidDataException(
                        "Installer payload is missing, duplicates, or exceeds the size limit for " +
                        file);
                }
            }

            LocalFiles.CheckParents(Path.Combine(destination, "check"));
            Directory.CreateDirectory(destination);
            long expanded = 0;
            foreach (string file in RuntimeService.PayloadFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CopyEntry(zip.GetEntry(file)!, Path.Combine(destination, file),
                    MaximumInstallerEntryBytes * RuntimeService.PayloadFiles.Length,
                    ref expanded, cancellationToken);
            }

            return;
        }

        throw new InvalidDataException("No appended ReShade ZIP payload was found.");
    }

    private static void CopyEntry(
        ZipArchiveEntry entry,
        string target,
        long maximumBytes,
        ref long expanded,
        CancellationToken cancellationToken)
    {
        LocalFiles.CheckParents(target);
        using Stream source = entry.Open();
        using FileStream output = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] buffer = new byte[81920];
        long actual = 0;
        int count;
        while ((count = source.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            actual += count;
            expanded += count;
            if (actual > entry.Length || expanded > maximumBytes)
            {
                throw new InvalidDataException("Archive entry exceeds its declared size or extraction limit.");
            }
            output.Write(buffer, 0, count);
        }

        if (actual != entry.Length)
        {
            throw new InvalidDataException("Archive entry does not match its declared size.");
        }
    }

    private sealed class PayloadStream(FileStream input, long start) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => input.Length - start;
        public override long Position
        {
            get => input.Position - start;
            set
            {
                if (value < 0 || value > Length)
                {
                    throw new IOException("Invalid installer archive position.");
                }
                input.Position = start + value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin)
        {
            long basis = origin switch
            {
                SeekOrigin.Begin => 0,
                SeekOrigin.Current => Position,
                SeekOrigin.End => Length,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            Position = checked(basis + offset);
            return Position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
