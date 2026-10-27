namespace ReShadeCentralized.Tests;

internal static partial class TestSuite
{
    private static Task OfficialInstallersExtractBothArchitecturesAndVulkanAsync(string installers)
    {
        runtimePayload = CreateTestDirectory("official-payloads");
        foreach (RuntimeBuild build in Enum.GetValues<RuntimeBuild>())
        {
            string source = Path.Combine(
                installers,
                build == RuntimeBuild.Standard ?
                "ReShade_Setup_6.8.0.exe" :
                "ReShade_Setup_6.8.0_Addon.exe");
            string destination = Path.Combine(runtimePayload, build.ToString());
            SafeArchive.ExtractInstaller(source, destination);
            AssertTrue(RuntimeService.ValidatePayload(destination) == "6.8.0");
            AssertTrue(PortableExecutable.Architecture(Path.Combine(
                        destination,
                        "ReShade32.dll")) == CpuArchitecture.X86);
            AssertTrue(PortableExecutable.Architecture(Path.Combine(
                        destination,
                        "ReShade64.dll")) == CpuArchitecture.X64);
        }

        return Task.CompletedTask;
    }
}
