namespace ReShadeCentralized;

public static class ProfilePaths
{
    public static string DirectoryFor(ManagerState state, GameProfile game)
    {
        if (game.Directory.Length > 0)
        {
            return game.Directory;
        }

        if (string.IsNullOrWhiteSpace(game.Executable))
        {
            throw new FileNotFoundException("Select the game's executable before registering it.");
        }

        string name = Path.GetFileNameWithoutExtension(game.Executable);
        return FileTransaction.Contained(state.Paths.Profiles, name + "_" + game.Id);
    }
}
