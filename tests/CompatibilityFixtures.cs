namespace ReShadeCentralized.Tests;

internal static partial class TestSuite
{
    // Small authored test cases for supported rules, not a redistributed upstream catalog snapshot.
    private const string CompatibilityFixture = """
        [bms.exe]
        RenderApi=D3D9
        InstallTarget=bin
        DepthReversed=0
        DepthCopyBeforeClears=1
        [RDR2.exe]
        RenderApi=D3D12
        ForceInstallApi=1
        DepthReversed=1
        [Claire de Lune.exe]
        RenderApi=D3D12
        InstallTarget=runtime\bin
        Is64Bit=1
        [Dead Space.exe]
        RenderApi=D3D9
        DepthReversed=0
        DepthCopyBeforeClears=1
        [BlockedFixture.exe]
        Banned=1
        """;

    private static void CacheCompatibilityFixture(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, CompatibilityCatalog.CacheName);
        File.WriteAllText(path, CompatibilityFixture);
        CompatibilityCatalog.LoadCache(path);
    }
}
