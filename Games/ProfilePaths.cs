namespace ReShadeCentralized;

public static class ProfilePaths
{
    public static string DirectoryFor(ManagerState state, GameProfile game, string? executable = null)
    {
        if (game.Directory.Length > 0)
        {
            return game.Directory;
        }

        string name = Path.GetFileNameWithoutExtension(executable ?? game.Executable);
        return LocalFiles.Contained(state.Paths.Profiles, name + "_" + game.Id);
    }
}
