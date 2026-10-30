using System.IO.Compression;
using System.Text.RegularExpressions;

namespace ReShadeCentralized;

public static class SafeArchive
{
    public static void Extract(string archivePath, string destination, CancellationToken cancellationToken = default)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        Extract(archive, destination, cancellationToken);
    }

    public static void Extract(ZipArchive archive, string destination, CancellationToken cancellationToken = default)
    {
        long expanded = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Validate the entire archive before writing anything.
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = entry.FullName.Replace('\\', '/');
            if (name.Split('/').Any(value => value == ".." || value.Contains(':') || value.TrimEnd(' ', '.') != value ||
                Regex.IsMatch(value, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", RegexOptions.IgnoreCase)))
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
            if (expanded > 16L * 1024 * 1024 * 1024 || archive.Entries.Count > 100000)
            {
                throw new InvalidDataException("Archive exceeds extraction limits.");
            }

            LocalFiles.CheckParents(target);
            if (LocalFiles.Exists(target))
            {
                throw new IOException("Archive extraction requires a fresh directory: " + target);
            }
        }

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string target = LocalFiles.Contained(
                destination,
                entry.FullName.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
            if (entry.Name.Length == 0)
            {
                Directory.CreateDirectory(target);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using Stream source = entry.Open();
                using FileStream output = File.Create(target);
                byte[] buffer = new byte[81920];
                int count;
                while ((count = source.Read(buffer)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, count);
                }
            }
        }
    }

    public static void ExtractInstaller(string installer, string destination)
    {
        using FileStream input = File.OpenRead(installer);
        byte[] block = new byte[512];
        while (input.Read(block) == block.Length)
        {
            if (block[0] != 0x50 || block[1] != 0x4B || block[2] != 3 || block[3] != 4 ||
                !block.Skip(4).Take(26).Any(item => item != 0))
            {
                continue;
            }

            using var payload = new MemoryStream();
            payload.Write(block);
            input.CopyTo(payload);
            payload.Position = 0;
            using var zip = new ZipArchive(payload, ZipArchiveMode.Read);
            foreach (string file in RuntimeService.PayloadFiles)
            {
                ZipArchiveEntry[] entries = zip.Entries.Where(item => item.FullName == file).ToArray();
                if (entries.Length != 1 || entries[0].Length > 128L * 1024 * 1024)
                {
                    throw new InvalidDataException(
                        "Installer payload is missing, duplicates, or exceeds the size limit for " +
                        file);
                }
            }

            // Extract only supported payloads, never execute bundled tools or add-ons.
            Directory.CreateDirectory(destination);
            foreach (string file in RuntimeService.PayloadFiles)
            {
                zip.GetEntry(file)!.ExtractToFile(Path.Combine(destination, file));
            }

            return;
        }

        throw new InvalidDataException("No appended ReShade ZIP payload was found.");
    }
}
