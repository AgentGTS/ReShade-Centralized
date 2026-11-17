using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Text;

namespace ReShadeCentralized;

public static class PortableExecutable {
    public static string? GetValidatedGamePath(string path) => GetValidatedGamePath(path, out _);

    internal static string? GetValidatedGamePath(string path, out CpuArchitecture architecture) {
        architecture = default;
        try {
            string? executable = ValidateGamePath(path);
            if (executable == null)
                return null;

            using FileStream stream = OpenGame(executable);
            using var reader = new PEReader(stream);
            PEHeaders headers = reader.PEHeaders;
            if (GameImage(headers) == null)
                return null;

            architecture = headers.CoffHeader.Machine == Machine.Amd64 ? CpuArchitecture.X64 : CpuArchitecture.X86;
            return executable;
        } catch (Exception error) when (IsGameReadError(error)) {
            return null;
        }
    }

    internal static string? ValidateGamePath(string path) {
        path = path.Trim();
        if (path.Length < 4
            || !char.IsAsciiLetter(path[0])
            || path[1] != ':'
            || (path[2] != '\\' && path[2] != '/')
            || path.AsSpan(2).Contains(':')
            || path.IndexOfAny(['*', '?', '"', '<', '>', '|', '\0']) >= 0
            || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return null;

        string[] parts = path[3..].Split(['\\', '/']);
        if (parts.Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')))
            return null;

        path = Path.GetFullPath(path);
        string root = Path.GetPathRoot(path)!;
        if (new DriveInfo(root).DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.CDRom or
            DriveType.Ram))
            return null;

        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (windows.Length > 0 && path.StartsWith(windows.TrimEnd('\\', '/') + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase))
            return null;

        // Check each parent before entering it so links cannot redirect inspection to another location.
        string parent = root;
        foreach (string part in parts[..^1]) {
            parent = Path.Combine(parent, part);
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                return null;
        }

        FileAttributes attributes = File.GetAttributes(path);
        return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) == 0
            ? path : null;
    }

    internal static FileStream OpenGame(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    internal static PEHeader? GameImage(PEHeaders headers) =>
        headers.CoffHeader.Machine is Machine.I386 or Machine.Amd64
        && headers.CoffHeader.Characteristics.HasFlag(Characteristics.ExecutableImage)
        && !headers.CoffHeader.Characteristics.HasFlag(Characteristics.Dll) ? headers.PEHeader : null;

    internal static bool IsGameReadError(Exception error) => error is IOException or InvalidDataException
        or UnauthorizedAccessException or BadImageFormatException or ArgumentException or NotSupportedException
        or System.Security.SecurityException;

    public static CpuArchitecture Architecture(string path) {
        using FileStream stream = File.OpenRead(path);
        using var reader = new PEReader(stream);
        if (reader.PEHeaders.PEHeader == null)
            throw new InvalidDataException("Not a PE executable: " + path);

        return reader.PEHeaders.CoffHeader.Machine switch {
            Machine.I386 => CpuArchitecture.X86,
            Machine.Amd64 => CpuArchitecture.X64,
            _ => throw new NotSupportedException("Only x86 and x64 games and ReShade binaries are supported.")
        };
    }

    public static bool IsReShade(string path) =>
        File.Exists(path)
        && FileVersionInfo.GetVersionInfo(path).ProductName == "ReShade";
}

public static class GraphicsApiDetector {
    public static RenderApi? Detect(string path, CancellationToken cancellationToken = default) {
        try {
            cancellationToken.ThrowIfCancellationRequested();
            string? executable = PortableExecutable.ValidateGamePath(path);
            if (executable == null)
                return null;

            using FileStream stream = PortableExecutable.OpenGame(executable);
            using var reader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            PEHeaders headers = reader.PEHeaders;
            if (PortableExecutable.GameImage(headers) is not { } image)
                return null;

            var candidates = new HashSet<RenderApi>();
            ReadImports(stream, headers, image.ImportTableDirectory, false, candidates, cancellationToken);
            ReadImports(stream, headers, image.DelayImportTableDirectory, true, candidates, cancellationToken);
            return candidates.Count == 1 ? candidates.Single() : null;
        } catch (Exception error) when (error is OverflowException || PortableExecutable.IsGameReadError(error)) {
            return null;
        }
    }

    private static void ReadImports(
        FileStream stream,
        PEHeaders headers,
        DirectoryEntry directory,
        bool delayLoaded,
        HashSet<RenderApi> candidates,
        CancellationToken cancellationToken) {
        if (directory.RelativeVirtualAddress == 0 && directory.Size == 0)
            return;

        int descriptorSize = delayLoaded ? 32 : 20;
        int descriptorCount = directory.Size / descriptorSize;
        if (directory.RelativeVirtualAddress <= 0 || descriptorCount is < 1 or > 4096)
            throw new InvalidDataException("Invalid import directory.");

        Span<byte> descriptor = stackalloc byte[32];
        for (var index = 0; index < descriptorCount; index++) {
            cancellationToken.ThrowIfCancellationRequested();
            long address = (long)directory.RelativeVirtualAddress + ((long)index * descriptorSize);
            (long offset, int available) = LocateData(stream, headers, address);
            if (available < descriptorSize)
                throw new InvalidDataException("Truncated import descriptor.");

            stream.Position = offset;
            Span<byte> data = descriptor[..descriptorSize];
            stream.ReadExactly(data);
            if (data.IndexOfAnyExcept((byte)0) < 0)
                return;

            long nameAddress = BinaryPrimitives.ReadUInt32LittleEndian(data[(delayLoaded ? 4 : 12)..]);
            if (delayLoaded) {
                uint attributes = BinaryPrimitives.ReadUInt32LittleEndian(data);
                if (attributes == 0)
                    nameAddress = checked(nameAddress - (long)headers.PEHeader!.ImageBase);
                else if (attributes != 1)
                    throw new InvalidDataException("Unsupported delay import attributes.");
            }

            string module = ReadModuleName(stream, headers, nameAddress);
            RenderApi? api = module.ToLowerInvariant() switch {
                "d3d9.dll" => RenderApi.Direct3D9,
                "dxgi.dll" or "d3d10.dll" or "d3d10_1.dll" or "d3d11.dll" or "d3d12.dll" => RenderApi.DXGI,
                "opengl32.dll" => RenderApi.OpenGL,
                "vulkan-1.dll" => RenderApi.Vulkan,
                _ => null
            };
            if (api.HasValue)
                candidates.Add(api.Value);
        }

        throw new InvalidDataException("Unterminated import directory.");
    }

    private static string ReadModuleName(FileStream stream, PEHeaders headers, long address) {
        (long offset, int available) = LocateData(stream, headers, address);
        stream.Position = offset;
        Span<byte> name = stackalloc byte[260];
        for (var index = 0; index < Math.Min(available, name.Length); index++) {
            int value = stream.ReadByte();
            if (value == 0)
                return Encoding.ASCII.GetString(name[..index]);

            if (value < 0 || value > 127)
                break;
            name[index] = (byte)value;
        }

        throw new InvalidDataException("Invalid imported module name.");
    }

    private static (long Offset, int Available) LocateData(FileStream stream, PEHeaders headers, long address) {
        foreach (SectionHeader section in headers.SectionHeaders) {
            long relative = address - section.VirtualAddress;
            if (relative < 0 || relative >= section.SizeOfRawData)
                continue;

            long offset = section.PointerToRawData + relative;
            long available = Math.Min(section.SizeOfRawData - relative, stream.Length - offset);
            if (offset < 0 || available <= 0)
                break;

            return (offset, checked((int)available));
        }

        throw new InvalidDataException("Import address is outside the executable's file data.");
    }
}
