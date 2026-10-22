using System.Diagnostics;
using System.Reflection.PortableExecutable;

namespace ReShadeCentralized;

public static class PortableExecutable
{
    public static CpuArchitecture Architecture(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using var reader = new PEReader(stream);
        if (reader.PEHeaders.PEHeader == null)
        {
            throw new InvalidDataException("Not a PE executable: " + path);
        }

        return reader.PEHeaders.CoffHeader.Machine switch
        {
            Machine.I386 => CpuArchitecture.X86,
            Machine.Amd64 => CpuArchitecture.X64,
            _ => throw new NotSupportedException("Only x86 and x64 games and ReShade binaries are supported.")
        };
    }

    public static bool IsReShade(string path) =>
        File.Exists(path) &&
        FileVersionInfo.GetVersionInfo(path).ProductName == "ReShade";
}
