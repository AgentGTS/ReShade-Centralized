namespace ReShadeCentralized;

public enum RenderApi {
    Direct3D9,
    DXGI,
    OpenGL,
    Vulkan
}

public enum CpuArchitecture {
    X86,
    X64
}

public sealed class GameProfile {
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Executable { get; set; } = "";

    public RenderApi Api { get; set; }

    public RuntimeBuild Build { get; set; }

    public CpuArchitecture Architecture { get; set; }

    public string Directory { get; set; } = "";
    public List<OwnedLink> Links { get; set; } = [];

    public GameProfile Clone() {
        var copy = (GameProfile)MemberwiseClone();
        copy.Links = [.. Links];
        return copy;
    }
}

public sealed record OwnedLink(string Path, string Target);

public static class ProfilePaths {
    public static string DirectoryFor(ManagerState state, GameProfile game, string? executable = null) {
        if (game.Directory.Length > 0)
            return game.Directory;

        string name = Path.GetFileNameWithoutExtension(executable ?? game.Executable);
        return LocalFiles.Contained(state.Paths.Profiles, name + "_" + game.Id);
    }
}
