namespace ReShadeCentralized;

public static class DisplayNames
{
    public static string For(RuntimeBuild build) => build switch
    {
        RuntimeBuild.Standard => "Standard",
        RuntimeBuild.FullAddon => "Standard + add-on support",
        _ => build.ToString()
    };

    public static string For(RenderApi api) => api switch
    {
        RenderApi.Direct3D9 => "DirectX 9",
        RenderApi.DXGI => "DXGI (DirectX 10/11/12)",
        _ => api.ToString()
    };
}
