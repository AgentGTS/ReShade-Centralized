using System.Buffers.Binary;
using System.Text;

namespace ReShadeCentralized.Tests;

internal static partial class TestSuite
{
    private static Task GamePathValidationRejectsUnsafeAndNonExecutablePathsAsync()
    {
        string directory = CreateTestDirectory("game-paths");
        string executable = Path.Combine(directory, "game.exe");
        File.WriteAllBytes(executable, CreateExecutableWithImports(true, ["dxgi.dll"]));
        File.WriteAllText(Path.Combine(directory, "dxgi.dll"), "Unrelated DLL; never load this file");
        File.WriteAllText(Path.Combine(directory, "ReShade.ini"), "[GENERAL]\nPerformanceMode=1");
        string malformed = Path.Combine(directory, "malformed.exe");
        File.WriteAllText(malformed, "Not a PE executable");
        string wrongExtension = Path.Combine(directory, "game.txt");
        File.Copy(executable, wrongExtension);
        string subdirectory = Path.Combine(directory, "folder.exe");
        Directory.CreateDirectory(subdirectory);
        string[] rejected =
        [
            "", "game.exe", @"C:game.exe", @"\game.exe", @"\\server\share\game.exe",
            @"\\?\C:\game.exe", @"\\.\pipe\game.exe", "file:///C:/game.exe", "https://example.com/game.exe",
            executable + ":stream.exe", executable + "\0", executable + " --argument", "\"" + executable + "\"",
            Path.Combine(directory, "..", Path.GetFileName(directory), "game.exe"),
            Path.Combine(directory, "missing.exe"), malformed, wrongExtension, subdirectory,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "cmd.exe")
        ];
        foreach (string path in rejected)
        {
            AssertTrue(PortableExecutable.GetValidatedGamePath(path) == null, "Unsafe game path must be rejected.");
            AssertTrue(GraphicsApiDetector.Detect(path) == null);
            AssertTrue(DeploymentService.InspectExistingInstallation(path, RenderApi.DXGI, []) == null,
                "Invalid paths must not trigger a neighboring DLL or configuration inspection.");
        }

        foreach (bool is64Bit in new[] { false, true })
        {
            byte[] image = CreateExecutableWithImports(is64Bit, ["dxgi.dll"]);
            File.WriteAllBytes(executable, image);
            AssertTrue(PortableExecutable.GetValidatedGamePath("  " + executable + "  ") == executable);
            AssertTrue(DeploymentService.InspectExistingInstallation(executable, RenderApi.DXGI, []) != null);
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x96), 0x2102);
            File.WriteAllBytes(executable, image);
            AssertTrue(PortableExecutable.GetValidatedGamePath(executable) == null, "A renamed DLL is not a game exe.");
            AssertTrue(GraphicsApiDetector.Detect(executable) == null);
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x96), 0x100);
            File.WriteAllBytes(executable, image);
            AssertTrue(PortableExecutable.GetValidatedGamePath(executable) == null);
        }
        return Task.CompletedTask;
    }

    private static Task GamePathValidationRejectsLinksAsync()
    {
        string directory = CreateTestDirectory("game-path-links");
        string targetDirectory = Path.Combine(directory, "target");
        Directory.CreateDirectory(targetDirectory);
        string executable = Path.Combine(targetDirectory, "game.exe");
        File.WriteAllBytes(executable, CreateExecutableWithImports(true, ["dxgi.dll"]));
        string link = Path.Combine(directory, "linked.exe");
        File.CreateSymbolicLink(link, executable);
        string parentLink = Path.Combine(directory, "linked-folder");
        Directory.CreateSymbolicLink(parentLink, targetDirectory);
        foreach (string path in new[] { link, Path.Combine(parentLink, "game.exe") })
        {
            AssertTrue(PortableExecutable.GetValidatedGamePath(path) == null);
            AssertTrue(GraphicsApiDetector.Detect(path) == null);
            AssertTrue(DeploymentService.InspectExistingInstallation(path, RenderApi.DXGI, []) == null);
        }
        AssertTrue(PortableExecutable.GetValidatedGamePath(executable) == executable);
        return Task.CompletedTask;
    }

    private static byte[] CreateExecutableWithImports(
        bool is64Bit,
        string[] imports,
        string[]? delayImports = null,
        bool legacyDelayAddresses = false)
    {
        var image = new byte[0x1200];
        void Write16(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(offset), value);
        void Write32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset), value);
        Write16(0, 0x5a4d);
        Write32(0x3c, 0x80);
        Write32(0x80, 0x00004550);
        Write16(0x84, is64Bit ? (ushort)0x8664 : (ushort)0x14c);
        Write16(0x86, 1);
        int optionalSize = is64Bit ? 240 : 224;
        Write16(0x94, (ushort)optionalSize);
        Write16(0x96, 0x102);
        const int optional = 0x98;
        Write16(optional, is64Bit ? (ushort)0x20b : (ushort)0x10b);
        if (is64Bit)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(optional + 24), 0x140000000);
        }
        else
        {
            Write32(optional + 28, 0x400000);
        }

        Write32(optional + 32, 0x1000);
        Write32(optional + 36, 0x200);
        Write32(optional + 56, 0x2000);
        Write32(optional + 60, 0x200);
        Write32(optional + (is64Bit ? 108 : 92), 16);
        int directories = optional + (is64Bit ? 112 : 96);
        int section = optional + optionalSize;
        Encoding.ASCII.GetBytes(".rdata").CopyTo(image, section);
        Write32(section + 8, 0x1000);
        Write32(section + 12, 0x1000);
        Write32(section + 16, 0x1000);
        Write32(section + 20, 0x200);
        Write32(section + 36, 0x40000040);
        int nextName = 0x600;

        uint AddName(string name)
        {
            uint address = (uint)(nextName - 0x200 + 0x1000);
            byte[] bytes = Encoding.ASCII.GetBytes(name + "\0");
            bytes.CopyTo(image, nextName);
            nextName += bytes.Length;
            return address;
        }

        if (imports.Length > 0)
        {
            Write32(directories + 8, 0x1000);
            Write32(directories + 12, (uint)((imports.Length + 1) * 20));
            for (var index = 0; index < imports.Length; index++)
            {
                Write32(0x200 + (index * 20) + 12, AddName(imports[index]));
            }
        }

        if (delayImports is { Length: > 0 })
        {
            Write32(directories + (13 * 8), 0x1200);
            Write32(directories + (13 * 8) + 4, (uint)((delayImports.Length + 1) * 32));
            for (var index = 0; index < delayImports.Length; index++)
            {
                uint address = AddName(delayImports[index]);
                Write32(0x400 + (index * 32), legacyDelayAddresses ? 0U : 1U);
                Write32(0x400 + (index * 32) + 4, legacyDelayAddresses ? address + 0x400000 : address);
            }
        }

        return image;
    }

    private static Task GraphicsApiDetectionFindsOnlyUnambiguousImportsAsync()
    {
        string directory = CreateTestDirectory("api-imports");
        string path = Path.Combine(directory, "game.exe");
        (string[] Imports, RenderApi? Expected)[] cases =
        [
            (["D3D9.DLL", "kernel32.dll"], RenderApi.Direct3D9),
            (["dxgi.dll", "d3d11.dll", "d3d12.dll"], RenderApi.DXGI),
            (["d3d10.dll", "d3d10_1.dll"], RenderApi.DXGI),
            (["opengl32.dll"], RenderApi.OpenGL),
            (["vulkan-1.dll"], RenderApi.Vulkan),
            (["dxgi.dll", "vulkan-1.dll"], null),
            (["opengl32.dll", "d3d9.dll"], null),
            (["kernel32.dll", "user32.dll"], null),
            ([], null)
        ];
        foreach (bool is64Bit in new[] { false, true })
        {
            foreach ((string[] imports, RenderApi? expected) in cases)
            {
                File.WriteAllBytes(path, CreateExecutableWithImports(is64Bit, imports));
                AssertTrue(GraphicsApiDetector.Detect(path) == expected);
            }

            File.WriteAllBytes(path, CreateExecutableWithImports(is64Bit, ["kernel32.dll"], ["vulkan-1.dll"]));
            AssertTrue(GraphicsApiDetector.Detect(path) == RenderApi.Vulkan);
            File.WriteAllBytes(path, CreateExecutableWithImports(is64Bit, ["d3d11.dll"], ["dxgi.dll"]));
            AssertTrue(GraphicsApiDetector.Detect(path) == RenderApi.DXGI);
            File.WriteAllBytes(path, CreateExecutableWithImports(is64Bit, ["d3d11.dll"], ["vulkan-1.dll"]));
            AssertTrue(GraphicsApiDetector.Detect(path) == null);
        }

        File.WriteAllBytes(path, CreateExecutableWithImports(false, [], ["opengl32.dll"], true));
        AssertTrue(GraphicsApiDetector.Detect(path) == RenderApi.OpenGL);
        byte[] stringsOnly = CreateExecutableWithImports(true, ["kernel32.dll"]);
        Encoding.ASCII.GetBytes("d3d12.dll\0vulkan-1.dll\0").CopyTo(stringsOnly, 0x800);
        File.WriteAllBytes(path, stringsOnly);
        AssertTrue(GraphicsApiDetector.Detect(path) == null);
        return Task.CompletedTask;
    }

    private static Task GraphicsApiDetectionFailsOpenAndHonorsCancellationAsync()
    {
        string directory = CreateTestDirectory("api-invalid");
        string path = Path.Combine(directory, "game.exe");
        AssertTrue(GraphicsApiDetector.Detect(path) == null);
        File.WriteAllText(path, "Not an executable");
        AssertTrue(GraphicsApiDetector.Detect(path) == null);
        byte[] image = CreateExecutableWithImports(true, ["d3d12.dll"]);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x200 + 12), 0xfffffff0);
        File.WriteAllBytes(path, image);
        AssertTrue(GraphicsApiDetector.Detect(path) == null);
        image = CreateExecutableWithImports(true, ["d3d12.dll"]);
        Array.Fill(image, (byte)'X', 0x600, 260);
        File.WriteAllBytes(path, image);
        AssertTrue(GraphicsApiDetector.Detect(path) == null);
        image = CreateExecutableWithImports(true, ["d3d12.dll"], ["vulkan-1.dll"]);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x400), 2);
        File.WriteAllBytes(path, image);
        AssertTrue(GraphicsApiDetector.Detect(path) == null);
        File.WriteAllBytes(path, CreateExecutableWithImports(true, ["d3d12.dll"])[..0x205]);
        AssertTrue(GraphicsApiDetector.Detect(path) == null);
        File.WriteAllBytes(path, CreateExecutableWithImports(true, ["d3d12.dll"]));
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            AssertTrue(GraphicsApiDetector.Detect(path) == null);
        }

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        AssertThrows<OperationCanceledException>(() => GraphicsApiDetector.Detect(path, cancellation.Token));
        return Task.CompletedTask;
    }

    private static Task GraphicsApiDetectionRejectsInvalidDirectoriesAndMachinesAsync()
    {
        string directory = CreateTestDirectory("api-directories");
        string path = Path.Combine(directory, "game.exe");
        foreach (bool is64Bit in new[] { false, true })
        {
            int importDirectory = 0x98 + (is64Bit ? 112 : 96) + 8;
            (int Offset, uint Value)[] invalidFields =
            [
                (importDirectory, 0),
                (importDirectory, 0x7fffffff),
                (importDirectory + 4, 19),
                (importDirectory + 4, 20),
                (importDirectory + 4, 20 * 4097),
                (0x200 + 12, 0x1fff)
            ];
            foreach ((int offset, uint value) in invalidFields)
            {
                byte[] image = CreateExecutableWithImports(is64Bit, ["d3d12.dll"]);
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset), value);
                image[^1] = (byte)'X';
                File.WriteAllBytes(path, image);
                AssertTrue(GraphicsApiDetector.Detect(path) == null,
                    $"Malformed PE field at {offset:x} must not produce a recommendation.");
            }

            byte[] unsupported = CreateExecutableWithImports(is64Bit, ["d3d12.dll"]);
            BinaryPrimitives.WriteUInt16LittleEndian(unsupported.AsSpan(0x84), 0xaa64);
            File.WriteAllBytes(path, unsupported);
            AssertTrue(GraphicsApiDetector.Detect(path) == null);
            byte[] invalidName = CreateExecutableWithImports(is64Bit, ["d3d12.dll"]);
            invalidName[0x600] = 0xff;
            File.WriteAllBytes(path, invalidName);
            AssertTrue(GraphicsApiDetector.Detect(path) == null);
        }

        byte[] legacyDelay = CreateExecutableWithImports(false, [], ["opengl32.dll"], true);
        BinaryPrimitives.WriteUInt32LittleEndian(legacyDelay.AsSpan(0x404), 0x10);
        File.WriteAllBytes(path, legacyDelay);
        AssertTrue(GraphicsApiDetector.Detect(path) == null);
        return Task.CompletedTask;
    }

    private static Task GraphicsApiDetectionLeavesFilesUnchangedAndReleasesHandlesAsync()
    {
        string directory = CreateTestDirectory("api-read-only");
        string path = Path.Combine(directory, "game.exe");
        File.WriteAllBytes(path, CreateExecutableWithImports(true, ["d3d11.dll"], ["dxgi.dll"]));
        string originalHash = FileTransaction.Hash(path);
        DateTime originalWriteTime = File.GetLastWriteTimeUtc(path);
        FileAttributes originalAttributes = File.GetAttributes(path);
        File.SetAttributes(path, originalAttributes | FileAttributes.ReadOnly);
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                AssertTrue(GraphicsApiDetector.Detect(path) == RenderApi.DXGI);
                using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                AssertTrue(exclusive.Length > 0);
            }
            AssertTrue(FileTransaction.Hash(path) == originalHash);
            AssertTrue(File.GetLastWriteTimeUtc(path) == originalWriteTime);
            AssertTrue(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
        }
        finally
        {
            File.SetAttributes(path, originalAttributes);
        }
        return Task.CompletedTask;
    }
}
