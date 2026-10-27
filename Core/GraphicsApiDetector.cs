using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;

namespace ReShadeCentralized;

public static class GraphicsApiDetector
{
    public static RenderApi? Detect(string path, CancellationToken cancellationToken = default) =>
        Detect(path, cancellationToken, useCompatibility: true);

    internal static RenderApi? DetectInternal(string path) =>
        Detect(path, CancellationToken.None, useCompatibility: false);

    private static RenderApi? Detect(string path, CancellationToken cancellationToken, bool useCompatibility)
    {
        string? executable = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            executable = PortableExecutable.GetValidatedGamePath(path);
            if (executable == null)
            {
                return null;
            }
            GameTarget target = GameTargetResolver.Resolve(executable, cancellationToken);
            executable = target.Executable;
            if (target.IsRemix || GameTargetResolver.HasDxvk(executable))
            {
                return RenderApi.Vulkan;
            }
            using var stream = new FileStream(executable, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            PEHeaders headers = reader.PEHeaders;
            if (headers.PEHeader == null || headers.CoffHeader.Machine is not (Machine.I386 or Machine.Amd64) ||
                !headers.CoffHeader.Characteristics.HasFlag(Characteristics.ExecutableImage) ||
                headers.CoffHeader.Characteristics.HasFlag(Characteristics.Dll))
            {
                return null;
            }

            var candidates = new HashSet<RenderApi>();
            ReadImports(stream, headers, headers.PEHeader.ImportTableDirectory, false, candidates, cancellationToken);
            ReadImports(stream, headers, headers.PEHeader.DelayImportTableDirectory,
                true, candidates, cancellationToken);
            return candidates.Count == 1 ? candidates.Single() : CatalogFallback();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or BadImageFormatException
            or ArgumentException or OverflowException or NotSupportedException or System.Security.SecurityException)
        {
            return CatalogFallback();
        }

        RenderApi? CatalogFallback() =>
            useCompatibility && executable != null ? CompatibilityCatalog.Default.Find(executable)?.Api : null;
    }

    private static void ReadImports(
        FileStream stream,
        PEHeaders headers,
        DirectoryEntry directory,
        bool delayLoaded,
        HashSet<RenderApi> candidates,
        CancellationToken cancellationToken)
    {
        if (directory.RelativeVirtualAddress == 0 && directory.Size == 0)
        {
            return;
        }

        int descriptorSize = delayLoaded ? 32 : 20;
        int descriptorCount = directory.Size / descriptorSize;
        if (directory.RelativeVirtualAddress <= 0 || descriptorCount is < 1 or > 4096)
        {
            throw new InvalidDataException("Invalid import directory.");
        }

        Span<byte> descriptor = stackalloc byte[32];
        for (var index = 0; index < descriptorCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long address = (long)directory.RelativeVirtualAddress + ((long)index * descriptorSize);
            (long offset, int available) = LocateData(stream, headers, address);
            if (available < descriptorSize)
            {
                throw new InvalidDataException("Truncated import descriptor.");
            }

            stream.Position = offset;
            Span<byte> data = descriptor[..descriptorSize];
            stream.ReadExactly(data);
            if (data.IndexOfAnyExcept((byte)0) < 0)
            {
                return;
            }

            long nameAddress = BinaryPrimitives.ReadUInt32LittleEndian(data[(delayLoaded ? 4 : 12)..]);
            if (delayLoaded)
            {
                uint attributes = BinaryPrimitives.ReadUInt32LittleEndian(data);
                if (attributes == 0)
                {
                    nameAddress = checked(nameAddress - (long)headers.PEHeader!.ImageBase);
                }
                else if (attributes != 1)
                {
                    throw new InvalidDataException("Unsupported delay import attributes.");
                }
            }

            string module = ReadModuleName(stream, headers, nameAddress);
            RenderApi? api = module.ToLowerInvariant() switch
            {
                "d3d9.dll" => RenderApi.Direct3D9,
                "dxgi.dll" or "d3d10.dll" or "d3d10_1.dll" or "d3d11.dll" or "d3d12.dll" => RenderApi.DXGI,
                "opengl32.dll" => RenderApi.OpenGL,
                "vulkan-1.dll" => RenderApi.Vulkan,
                _ => null
            };
            if (api.HasValue)
            {
                candidates.Add(api.Value);
            }
        }

        throw new InvalidDataException("Unterminated import directory.");
    }

    private static string ReadModuleName(FileStream stream, PEHeaders headers, long address)
    {
        (long offset, int available) = LocateData(stream, headers, address);
        stream.Position = offset;
        Span<byte> name = stackalloc byte[260];
        for (var index = 0; index < Math.Min(available, name.Length); index++)
        {
            int value = stream.ReadByte();
            if (value == 0)
            {
                return Encoding.ASCII.GetString(name[..index]);
            }

            if (value < 0 || value > 127)
            {
                break;
            }
            name[index] = (byte)value;
        }

        throw new InvalidDataException("Invalid imported module name.");
    }

    private static (long Offset, int Available) LocateData(FileStream stream, PEHeaders headers, long address)
    {
        foreach (SectionHeader section in headers.SectionHeaders)
        {
            long relative = address - section.VirtualAddress;
            if (relative < 0 || relative >= section.SizeOfRawData)
            {
                continue;
            }

            long offset = section.PointerToRawData + relative;
            long available = Math.Min(section.SizeOfRawData - relative, stream.Length - offset);
            if (offset < 0 || available <= 0)
            {
                break;
            }

            return (offset, checked((int)available));
        }

        throw new InvalidDataException("Import address is outside the executable's file data.");
    }
}
