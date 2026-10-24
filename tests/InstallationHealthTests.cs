namespace ReShadeCentralized.Tests;

internal static partial class TestSuite
{
    private static Task InstallationHealthDetectsMissingAndReplacedLinksAsync()
    {
        string directory = CreateTestDirectory("installation-health");
        string gameDirectory = Path.Combine(directory, "game");
        string profile = Path.Combine(directory, "profile");
        Directory.CreateDirectory(gameDirectory);
        Directory.CreateDirectory(profile);
        string executable = Path.Combine(gameDirectory, "game.exe");
        File.WriteAllBytes(executable, CreateExecutableWithImports(true, ["dxgi.dll"]));
        string target = Path.Combine(profile, "ReShade.ini");
        string localConfig = Path.Combine(gameDirectory, "ReShade.ini");
        File.WriteAllText(target, "[GENERAL]\nPerformanceMode=0\n");
        File.CreateSymbolicLink(localConfig, target);
        var game = new GameProfile
        {
            Executable = executable,
            Directory = profile,
            Api = RenderApi.Vulkan,
            Links = [new(localConfig, target)]
        };
        AssertTrue(DeploymentService.GetInstallationWarning(game) == null);
        File.AppendAllText(target, "PresetTransitionDuration=0\n");
        AssertTrue(DeploymentService.GetInstallationWarning(game) == null);

        File.Delete(localConfig);
        string warning = DeploymentService.GetInstallationWarning(game)!;
        AssertTrue(warning.Contains(localConfig) && warning.Contains("Repair / edit"));
        File.Copy(target, localConfig);
        AssertTrue(FileTransaction.Hash(localConfig) == FileTransaction.Hash(target));
        AssertTrue(DeploymentService.GetInstallationWarning(game)!.Contains("replaced"));
        string replacementHash = FileTransaction.Hash(localConfig);
        AssertTrue(DeploymentService.GetInstallationWarning(game) != null);
        AssertTrue(FileTransaction.Hash(localConfig) == replacementHash);

        string alternate = Path.Combine(profile, "Other.ini");
        File.Copy(target, alternate);
        File.Delete(localConfig);
        File.CreateSymbolicLink(localConfig, alternate);
        AssertTrue(DeploymentService.GetInstallationWarning(game)!.Contains(localConfig));
        AssertTrue(DeploymentService.IsOwnedLink(new(localConfig, alternate)));

        File.Delete(localConfig);
        File.CreateSymbolicLink(localConfig, target);
        File.Delete(target);
        AssertTrue(DeploymentService.GetInstallationWarning(game)!.Contains(target));
        File.WriteAllText(target, "[GENERAL]\n");
        AssertTrue(DeploymentService.GetInstallationWarning(game) == null);
        File.Delete(executable);
        AssertTrue(DeploymentService.GetInstallationWarning(game)!.Contains(executable));
        return Task.CompletedTask;
    }

    private static Task InstallationHealthChecksHooksAndUnconfiguredProfilesAsync()
    {
        string directory = CreateTestDirectory("hook-health");
        string executable = Path.Combine(directory, "game.exe");
        string runtime = Path.Combine(directory, "ReShade64.dll");
        string proxy = Path.Combine(directory, "dxgi.dll");
        File.WriteAllBytes(executable, CreateExecutableWithImports(true, ["dxgi.dll"]));
        File.WriteAllText(runtime, "runtime fixture");
        File.CreateSymbolicLink(proxy, runtime);
        var game = new GameProfile { Executable = executable, Links = [new(proxy, runtime)] };
        AssertTrue(DeploymentService.GetInstallationWarning(game) == null);
        File.Delete(proxy);
        Directory.CreateDirectory(proxy);
        AssertTrue(DeploymentService.GetInstallationWarning(game)!.Contains("replaced"));
        Directory.Delete(proxy);
        File.CreateSymbolicLink(proxy, runtime);
        AssertTrue(DeploymentService.GetInstallationWarning(game) == null);
        AssertTrue(DeploymentService.GetInstallationWarning(new GameProfile())!.Contains("executable"));
        AssertTrue(DeploymentService.GetInstallationWarning(new GameProfile { Executable = executable }) != null);
        return Task.CompletedTask;
    }
}
