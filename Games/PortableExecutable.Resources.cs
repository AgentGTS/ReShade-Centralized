using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;

namespace ReShadeCentralized;

public static partial class PortableExecutable
{
    internal static string? ReadResourceString(string path, int id)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var image = new PEReader(stream);
            if (image.PEHeaders.PEHeader is not { } header)
            {
                return null;
            }
            DirectoryEntry directory = header.ResourceTableDirectory;
            if (directory.RelativeVirtualAddress <= 0 || directory.Size is < 16 or > 16 * 1024 * 1024)
            {
                return null;
            }
            byte[] data = image.GetSectionData(directory.RelativeVirtualAddress)
                .GetContent(0, directory.Size).ToArray();
            int type = ResourceEntry(data, 0, 10, subdirectory: true);
            int name = ResourceEntry(data, type, id, subdirectory: true);
            int entry = ResourceEntry(data, name, null, subdirectory: false);
            if (entry < 0 || entry > data.Length - 16)
            {
                return null;
            }
            int address = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(entry)));
            int size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(entry + 4)));
            if (size is < 4 or > 32768 || size % 2 != 0)
            {
                return null;
            }
            byte[] text = image.GetSectionData(address).GetContent(0, size).ToArray();
            string result = new UnicodeEncoding(false, false, true).GetString(text);
            return result.EndsWith('\0') && !result[..^1].Contains('\0') ? result[..^1] : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException or
            ArgumentException or OverflowException or DecoderFallbackException)
        {
            return null;
        }
    }

    private static int ResourceEntry(byte[] data, int directory, int? id, bool subdirectory)
    {
        if (directory < 0 || directory > data.Length - 16)
        {
            return -1;
        }
        int count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(directory + 12)) +
            BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(directory + 14));
        if (count > 4096 || count > (data.Length - directory - 16) / 8)
        {
            return -1;
        }
        for (var index = 0; index < count; index++)
        {
            int offset = directory + 16 + index * 8;
            uint name = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
            uint target = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));
            if ((id == null || name == id) && ((target & 0x80000000) != 0) == subdirectory)
            {
                return checked((int)(target & 0x7fffffff));
            }
        }
        return -1;
    }
}
