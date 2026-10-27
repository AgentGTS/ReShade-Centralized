using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace ReShadeCentralized.Tests;

internal static partial class TestSuite
{
    private static byte[] ExecutableWithResource(bool is64Bit, int type, int id, byte[] data)
    {
        byte[] image = CreateExecutableWithImports(is64Bit, ["d3d9.dll"]);
        const int start = 0x800;
        int directory = 0x98 + (is64Bit ? 112 : 96) + 16;
        void Write16(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(offset), value);
        void Write32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset), value);
        Write32(directory, 0x1600);
        Write32(directory + 4, (uint)(88 + data.Length));
        Write16(start + 14, 1);
        Write32(start + 16, (uint)type);
        Write32(start + 20, 0x80000018);
        Write16(start + 24 + 14, 1);
        Write32(start + 40, (uint)id);
        Write32(start + 44, 0x80000030);
        Write16(start + 48 + 14, 1);
        Write32(start + 64, 0x409);
        Write32(start + 68, 72);
        Write32(start + 72, 0x1658);
        Write32(start + 76, (uint)data.Length);
        data.CopyTo(image, start + 88);
        return image;
    }

    private static byte[] BootstrapImage(string target) =>
        ExecutableWithResource(false, 10, 201, Encoding.Unicode.GetBytes(target + "\0"));

    private static byte[] DxvkImage(bool is64Bit)
    {
        string source = is64Bit ? Path.ChangeExtension(typeof(TestSuite).Assembly.Location, ".exe") :
            typeof(TestSuite).Assembly.Location;
        string product = FileVersionInfo.GetVersionInfo(source).ProductName!;
        byte[] image = File.ReadAllBytes(source);
        byte[] signature = Encoding.Unicode.GetBytes(product + "\0");
        for (var offset = 0; offset < image.Length;)
        {
            int match = image.AsSpan(offset).IndexOf(signature);
            if (match < 0)
            {
                break;
            }
            offset += match;
            image.AsSpan(offset, signature.Length).Clear();
            Encoding.Unicode.GetBytes("DXVK").CopyTo(image, offset);
            offset += signature.Length;
        }
        int header = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(0x3c));
        ushort characteristics = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(header + 22));
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(header + 22), (ushort)(characteristics | 0x2000));
        return image;
    }

    private static Task BootstrapResolutionUsesSafeTargetsAsync()
    {
        string directory = CreateTestDirectory("bootstrap-targets");
        string executable = Path.Combine(directory, "Launcher.exe");
        string target = Path.Combine(directory, "Game", "Binaries", "Win64", "Actual.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, CreateExecutableWithImports(true, ["dxgi.dll"]));
        File.WriteAllBytes(executable, BootstrapImage(Path.GetRelativePath(directory, target)));
        string hash = FileTransaction.Hash(executable);
        GameTarget resolved = GameTargetResolver.Resolve(executable);
        AssertTrue(resolved.Executable == target && resolved.Reason!.Contains("Unreal"));
        AssertTrue(GraphicsApiDetector.Detect(executable) == RenderApi.DXGI &&
            PortableExecutable.Architecture(resolved.Executable) == CpuArchitecture.X64);
        AssertTrue(FileTransaction.Hash(executable) == hash);
        using (File.Open(executable, FileMode.Open, FileAccess.Read, FileShare.None)) { }

        string outside = Path.Combine(Path.GetDirectoryName(directory)!, "outside.exe");
        File.WriteAllBytes(outside, CreateExecutableWithImports(true, ["opengl32.dll"]));
        foreach (string bad in new[]
            { "Missing.exe", outside, "..\\outside.exe", "Launcher.exe", "\\\\host\\game.exe" })
        {
            File.WriteAllBytes(executable, BootstrapImage(bad));
            AssertTrue(GameTargetResolver.Resolve(executable).Executable == executable);
        }
        string link = Path.Combine(directory, "Link.exe");
        File.CreateSymbolicLink(link, target);
        File.WriteAllBytes(executable, BootstrapImage("Link.exe"));
        AssertTrue(GameTargetResolver.Resolve(executable).Executable == executable);
        File.WriteAllBytes(executable, BootstrapImage("Next.exe"));
        File.WriteAllBytes(Path.Combine(directory, "Next.exe"), BootstrapImage("Launcher.exe"));
        AssertTrue(GameTargetResolver.Resolve(executable).Executable == executable);
        byte[] damaged = BootstrapImage("Next.exe");
        BinaryPrimitives.WriteUInt32LittleEndian(damaged.AsSpan(0x814), 0xfffffffc);
        File.WriteAllBytes(executable, damaged);
        AssertTrue(GameTargetResolver.Resolve(executable).Executable == executable);
        File.WriteAllBytes(executable, ExecutableWithResource(false, 10, 201, [1, 2, 3]));
        AssertTrue(GameTargetResolver.Resolve(executable).Executable == executable);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        AssertThrows<OperationCanceledException>(() => GameTargetResolver.Resolve(target, cancellation.Token));
        return Task.CompletedTask;
    }

    private static Task TranslationLayersRecommendEffectiveRendererAsync()
    {
        foreach (bool is64Bit in new[] { false, true })
        {
            string directory = CreateTestDirectory("dxvk-detection");
            string executable = Path.Combine(directory, "game.exe");
            File.WriteAllBytes(executable, CreateExecutableWithImports(is64Bit, ["d3d9.dll"]));
            string proxy = Path.Combine(directory, "d3d9.dll");
            File.WriteAllBytes(proxy, DxvkImage(is64Bit));
            AssertTrue(FileVersionInfo.GetVersionInfo(proxy).ProductName == "DXVK");
            string hash = FileTransaction.Hash(proxy);
            AssertTrue(GraphicsApiDetector.Detect(executable) == RenderApi.Vulkan);
            AssertTrue(DeploymentService.InspectExistingInstallation(executable, RenderApi.Vulkan, []) == null);
            AssertTrue(FileTransaction.Hash(proxy) == hash);
            File.WriteAllBytes(proxy, DxvkImage(!is64Bit));
            AssertTrue(GraphicsApiDetector.Detect(executable) == RenderApi.Direct3D9);
            File.WriteAllText(proxy, "Unrelated DLL");
            AssertTrue(GraphicsApiDetector.Detect(executable) == RenderApi.Direct3D9);
            File.Delete(proxy);
            string dxvk = Path.Combine(directory, "saved.dll");
            File.WriteAllBytes(dxvk, DxvkImage(is64Bit));
            File.CreateSymbolicLink(proxy, dxvk);
            AssertTrue(GraphicsApiDetector.Detect(executable) == RenderApi.Direct3D9);
        }

        string remix = CreateTestDirectory("remix-detection");
        string game = Path.Combine(remix, "game.exe");
        File.WriteAllBytes(game, CreateExecutableWithImports(false, ["d3d9.dll"]));
        string bridge = Path.Combine(remix, ".trex", "NvRemixBridge.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(bridge)!);
        File.WriteAllBytes(bridge, CreateExecutableWithImports(true, []));
        AssertTrue(GameTargetResolver.Resolve(game) is { Executable: var found, IsRemix: true } && found == bridge);
        AssertTrue(GraphicsApiDetector.Detect(game) == RenderApi.Vulkan &&
            GraphicsApiDetector.Detect(bridge) == RenderApi.Vulkan);
        File.WriteAllBytes(bridge, CreateExecutableWithImports(false, []));
        AssertTrue(GameTargetResolver.Resolve(game).Executable == game);
        File.WriteAllText(bridge, "Invalid bridge");
        AssertTrue(GameTargetResolver.Resolve(game).Executable == game);
        return Task.CompletedTask;
    }

    private static Task CompatibilityRulesValidatePathsAndPreserveSettingsAsync()
    {
        string directory = CreateTestDirectory("compatibility-rules");
        string executable = Path.Combine(directory, "game.exe");
        File.WriteAllBytes(executable, CreateExecutableWithImports(false, []));
        var catalog = new CompatibilityCatalog("[game.exe]\nRenderApi=D3D12\nForceInstallApi=1\n" +
            "InstallTarget=bin\nIs64Bit=1\nDepthReversed=1\nDepthCopyBeforeClears=2\n" +
            "DepthCopyAtClearIndex=4294967295\n");
        AssertTrue(catalog.Find("GAME.EXE")!.Api == RenderApi.DXGI);
        AssertTrue(catalog.InstallationDirectory(executable, RenderApi.DXGI) == Path.Combine(directory, "bin"));
        AssertTrue(catalog.Proxy(executable, RenderApi.DXGI) == "d3d12.dll" &&
            catalog.Proxy(executable, RenderApi.OpenGL) == "opengl32.dll");
        AssertTrue(catalog.Architecture(executable, RenderApi.DXGI) == CpuArchitecture.X64 &&
            catalog.Architecture(executable, RenderApi.Vulkan) == CpuArchitecture.X86);
        AssertTrue(catalog.InstallationDirectory(executable, RenderApi.Vulkan) == directory);
        foreach (string bad in new[] { "..\\escape", directory, "\\\\server\\share", "bin:stream", "bin\\..", "bin." })
        {
            var unsafeCatalog = new CompatibilityCatalog("[game.exe]\nInstallTarget=" + bad);
            AssertThrows<IOException>(() => unsafeCatalog.InstallationDirectory(executable, RenderApi.DXGI));
        }
        var config = new IniDocument();
        catalog.ApplyDefaults(config, executable);
        AssertTrue(config.Get("GENERAL", "PreprocessorDefinitions")!.Contains("RESHADE_DEPTH_INPUT_IS_REVERSED=1"));
        AssertTrue(config.Get("DEPTH", "DepthCopyBeforeClears") == "2" &&
            config.Get("DEPTH", "DepthCopyAtClearIndex") == "4294967295");
        config.Set("GENERAL", "PreprocessorDefinitions", "CUSTOM=42");
        config.Set("DEPTH", "DepthCopyBeforeClears", "0");
        catalog.ApplyDefaults(config, executable);
        AssertTrue(config.Get("GENERAL", "PreprocessorDefinitions") == "CUSTOM=42" &&
            config.Get("DEPTH", "DepthCopyBeforeClears") == "0");
        new CompatibilityCatalog("").ApplyDefaults(config, executable);
        AssertTrue(new CompatibilityCatalog("invalid catalog").Find(executable) == null);
        AssertTrue(CompatibilityCatalog.Default.Find("RDR2.exe") is { ForcedProxy: "d3d12.dll" });
        AssertTrue(CompatibilityCatalog.Default.Find("Claire de Lune.exe") is { Architecture: CpuArchitecture.X64 });
        var fixture = new IniDocument(CompatibilityFixture);
        foreach (string section in fixture.Sections)
        {
            foreach (string key in new[] { "DepthReversed", "DepthUpsideDown", "DepthLogarithmic",
                "DepthCopyBeforeClears", "DepthCopyAtClearIndex", "DrawStatsHeuristic", "UseAspectRatioHeuristics" })
            {
                if (fixture.Get(section, key) is { Length: > 0 } value)
                {
                    AssertTrue(CompatibilityCatalog.Default.Find(section)!
                        .DepthDefaults.GetValueOrDefault(key) == value,
                        "Cached depth values must be preserved: " + section + " / " + key);
                }
            }
        }
        File.Copy(executable, Path.Combine(directory, "RDR2.exe"));
        AssertTrue(GraphicsApiDetector.Detect(Path.Combine(directory, "RDR2.exe")) == RenderApi.DXGI);
        return Task.CompletedTask;
    }

    private static Task ApiRecommendationsPreferExecutableEvidenceAsync()
    {
        string directory = CreateTestDirectory("filename-api-collision");
        string executable = Path.Combine(directory, "Dead Space.exe");
        foreach (bool is64Bit in new[] { false, true })
        {
            foreach (bool delayed in new[] { false, true })
            {
                foreach (string module in new[] { "d3d9.dll", "dxgi.dll", "d3d12.dll" })
                {
                    File.WriteAllBytes(executable, CreateExecutableWithImports(is64Bit,
                        delayed ? [] : [module], delayed ? [module] : []));
                    RenderApi expected = module == "d3d9.dll" ? RenderApi.Direct3D9 : RenderApi.DXGI;
                    AssertTrue(GraphicsApiDetector.Detect(executable) == expected);
                    var config = new IniDocument();
                    CompatibilityCatalog.Default.ApplyDefaults(config, executable);
                    AssertTrue((config.Get("DEPTH", "DepthCopyBeforeClears") == "1") ==
                        (expected == RenderApi.Direct3D9));
                    AssertTrue((config.Get("GENERAL", "PreprocessorDefinitions") != null) ==
                        (expected == RenderApi.Direct3D9));
                }
            }
        }

        foreach (string[] imports in new[] { Array.Empty<string>(), new[] { "d3d9.dll", "dxgi.dll" } })
        {
            File.WriteAllBytes(executable, CreateExecutableWithImports(true, imports));
            AssertTrue(GraphicsApiDetector.Detect(executable) == RenderApi.Direct3D9);
            string unknown = Path.Combine(directory, "Unknown.exe");
            File.Copy(executable, unknown, overwrite: true);
            AssertTrue(GraphicsApiDetector.Detect(unknown) == null);
        }

        File.WriteAllBytes(executable, CreateExecutableWithImports(false, ["d3d9.dll"]));
        var conflicting = new CompatibilityCatalog("[Dead Space.exe]\nRenderApi=D3D12\n" +
            "InstallTarget=bin\nForceInstallApi=1\nIs64Bit=1\nDepthReversed=1\nDepthCopyBeforeClears=1\n");
        AssertTrue(conflicting.InstallationDirectory(executable, RenderApi.DXGI) == directory);
        AssertTrue(conflicting.Proxy(executable, RenderApi.DXGI) == "dxgi.dll");
        AssertTrue(conflicting.Architecture(executable, RenderApi.DXGI) == CpuArchitecture.X86);
        var preserved = new IniDocument("[GENERAL]\nPreprocessorDefinitions=CUSTOM=7\n[DEPTH]\nCustom=keep\n");
        string original = preserved.ToString();
        conflicting.ApplyDefaults(preserved, executable);
        AssertTrue(preserved.ToString() == original);
        return Task.CompletedTask;
    }

    private static Task StartupPresetMigrationPreservesNativeResolutionAsync()
    {
        foreach (string selection in new[] { "relative", "absolute", "empty", "missing" })
        {
            string directory = CreateTestDirectory("startup-preset-" + selection);
            ManagerState state = CreateState(Path.Combine(directory, "central"));
            GameProfile game = CreateRegistrationGame(state, Path.Combine(directory, "game"));
            string originalBase = Path.Combine(directory, "old-settings");
            Directory.CreateDirectory(originalBase);
            string startup = Path.Combine(originalBase, "Startup,night.ini");
            File.WriteAllText(startup, "Techniques=StartupEffect\n");
            string selected = selection switch
            {
                "absolute" => startup,
                "empty" => "",
                "missing" => ".\\Absent.ini",
                _ => ".\\Startup,night.ini"
            };
            string initial = "[INSTALL]\nBasePath=" + IniDocument.Join([originalBase]) +
                "\n[GENERAL]\nStartupPresetPath=" + IniDocument.Join([selected]) + "\nPresetPath=Current.ini\n";
            using (var transaction = new FileTransaction(Path.Combine(directory, "recovery"), "startup adoption"))
            {
                ConfigurationService.Update(state, game, transaction, initial);
                transaction.Commit();
            }
            string configPath = ConfigurationService.ConfigPath(game);
            IniDocument config = IniDocument.Read(configPath);
            string expected = selected.Length == 0 ? "" : Path.GetFullPath(selected, originalBase);
            AssertTrue(IniDocument.Split(config.Get("GENERAL", "StartupPresetPath")).FirstOrDefault() ==
                (expected.Length == 0 ? null : expected));
            if (selection is "relative" or "absolute")
            {
                string wrongBeforeFix = Path.GetFullPath(selected, game.Directory);
                AssertTrue(selection == "absolute" || wrongBeforeFix != startup);
                AssertTrue(File.ReadAllText(expected) == "Techniques=StartupEffect\n");
            }
            using (var repair = new FileTransaction(Path.Combine(directory, "recovery"), "startup repair"))
            {
                ConfigurationService.Update(state, game, repair);
                repair.Commit();
            }
            AssertTrue(IniDocument.Read(configPath).Get("GENERAL", "StartupPresetPath") ==
                config.Get("GENERAL", "StartupPresetPath"));
        }
        return Task.CompletedTask;
    }
}
