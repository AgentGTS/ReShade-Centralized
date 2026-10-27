namespace ReShadeCentralized;

public sealed record GameCompatibility(
    RenderApi? Api,
    string? InstallTarget,
    string? ForcedProxy,
    CpuArchitecture? Architecture,
    bool Banned,
    IReadOnlyDictionary<string, string> DepthDefaults);

public sealed class CompatibilityCatalog
{
    private readonly Dictionary<string, GameCompatibility> games = new(StringComparer.OrdinalIgnoreCase);
    public const string CatalogUrl =
        "https://raw.githubusercontent.com/crosire/reshade-shaders/list/Compatibility.ini";
    public const string CacheName = "CompatibilityCache.ini";
    public static CompatibilityCatalog Default { get; private set; } = new("");
    public int Count => games.Count;

    public CompatibilityCatalog(string text)
    {
        var document = new IniDocument(text);
        foreach (string executable in document.Sections.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Path.GetFileName(executable) != executable ||
                !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? api = document.Get(executable, "RenderApi");
            RenderApi? renderApi = api switch
            {
                "D3D8" or "D3D9" => RenderApi.Direct3D9,
                "D3D10" or "D3D11" or "D3D12" or "DXGI" => RenderApi.DXGI,
                "OpenGL" => RenderApi.OpenGL,
                "Vulkan" => RenderApi.Vulkan,
                _ => null
            };
            string? forcedProxy = document.Get(executable, "ForceInstallApi") == "1" ? api switch
            {
                "D3D10" => "d3d10.dll",
                "D3D11" => "d3d11.dll",
                "D3D12" => "d3d12.dll",
                _ => null
            } : null;
            CpuArchitecture? architecture = document.Get(executable, "Is64Bit") switch
            {
                "1" => CpuArchitecture.X64,
                "0" => CpuArchitecture.X86,
                _ => null
            };
            var depth = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in new[] { "DepthReversed", "DepthUpsideDown", "DepthLogarithmic",
                "DepthCopyBeforeClears", "DepthCopyAtClearIndex", "DrawStatsHeuristic", "UseAspectRatioHeuristics" })
            {
                string? value = document.Get(executable, key);
                bool bit = key is "DepthReversed" or "DepthUpsideDown" or "DepthLogarithmic" or
                    "UseAspectRatioHeuristics";
                if (value != null && (bit ? value is "0" or "1" :
                    uint.TryParse(value, out _)))
                {
                    depth[key] = value;
                }
            }
            games[executable] = new(renderApi, document.Get(executable, "InstallTarget"), forcedProxy,
                architecture, document.Get(executable, "Banned") == "1", depth);
        }
    }

    public static void LoadCache(string path, IProgress<string>? progress = null) =>
        Default = new(CatalogCache.Read(path, progress) ?? "");

    public static async Task RefreshAsync(
        Downloads downloads, string path, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        try
        {
            progress?.Report("Downloading game compatibility information from upstream...");
            string text = await CatalogCache.DownloadAsync(downloads, CatalogUrl, path, text =>
            {
                if (new CompatibilityCatalog(text).Count == 0)
                {
                    throw new InvalidDataException("Compatibility catalog has no executable entries.");
                }
            }, cancellationToken);
            Default = new(text);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or
            UnauthorizedAccessException ||
            (error is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            progress?.Report("Compatibility download unavailable; using the local cache when available. " +
                error.Message);
            LoadCache(path, progress);
            if (Default.Count == 0)
            {
                progress?.Report("No compatibility cache is available; " +
                    "use internal detection or choose the API manually.");
            }
        }
    }

    public GameCompatibility? Find(string executable) =>
        games.GetValueOrDefault(Path.GetFileName(executable));

    private GameCompatibility? Applicable(string executable)
    {
        GameCompatibility? game = Find(executable);
        if (game?.Api is not { } expectedApi)
        {
            return game;
        }

        RenderApi? detected = GraphicsApiDetector.DetectInternal(executable);
        return detected == null || detected == expectedApi ? game : null;
    }

    public string InstallationDirectory(string executable, RenderApi? api)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        string? target = api == RenderApi.Vulkan ? null : Applicable(executable)?.InstallTarget;
        if (string.IsNullOrEmpty(target))
        {
            return directory;
        }

        if (Path.IsPathRooted(target) || target.Split(['\\', '/']).Any(part =>
            part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
            part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new IOException("The compatibility install folder is unsafe: " + target);
        }
        return FileTransaction.Contained(directory, target);
    }

    public string? Proxy(string executable, RenderApi api) =>
        api == RenderApi.DXGI && Applicable(executable)?.ForcedProxy is string proxy ?
            proxy : DeploymentService.Proxy(api);

    public CpuArchitecture Architecture(string executable, RenderApi api) =>
        api != RenderApi.Vulkan &&
        Applicable(executable) is { InstallTarget.Length: > 0, Architecture: { } architecture }
            ? architecture : PortableExecutable.Architecture(executable);

    public void ApplyDefaults(IniDocument document, string executable)
    {
        if (Applicable(executable) is not { } game || game.DepthDefaults.Count == 0)
        {
            return;
        }

        if (game.DepthDefaults.Keys.Any(key => key is "DepthReversed" or "DepthUpsideDown" or "DepthLogarithmic"))
        {
            document.SetDefault("GENERAL", "PreprocessorDefinitions", IniDocument.Join([
                "RESHADE_DEPTH_LINEARIZATION_FAR_PLANE=1000.0",
                "RESHADE_DEPTH_INPUT_IS_UPSIDE_DOWN=" + game.DepthDefaults.GetValueOrDefault("DepthUpsideDown", "0"),
                "RESHADE_DEPTH_INPUT_IS_REVERSED=" + game.DepthDefaults.GetValueOrDefault("DepthReversed", "0"),
                "RESHADE_DEPTH_INPUT_IS_LOGARITHMIC=" + game.DepthDefaults.GetValueOrDefault("DepthLogarithmic", "0")
            ]));
        }
        foreach ((string key, string value) in game.DepthDefaults)
        {
            if (key is not ("DepthReversed" or "DepthUpsideDown" or "DepthLogarithmic"))
            {
                document.SetDefault("DEPTH", key, value);
            }
        }
    }
}
