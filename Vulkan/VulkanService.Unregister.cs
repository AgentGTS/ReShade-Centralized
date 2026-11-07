using System.Security.Cryptography;
using System.Text.Json;

namespace ReShadeCentralized;

public sealed partial class VulkanService
{
    public void UnregisterGame(
        ManagerState state,
        GameProfile game,
        FileTransaction transaction,
        UnregisterResult result)
    {
        if (game.Api != RenderApi.Vulkan)
        {
            return;
        }

        VulkanAppEntry[] owned = state.OwnedVulkanApps
            .Where(entry => SameUnregisterPath(entry.Executable, game.Executable))
            .ToArray();
        if (owned.Length == 0)
        {
            return;
        }

        string[] registeredLists;
        try
        {
            registeredLists = RegisteredLayers()
                .Select(AppsPath)
                .ToArray();
        }
        catch (Exception error) when (error is IOException
            or InvalidDataException
            or UnauthorizedAccessException
            or JsonException
            or InvalidOperationException
            or KeyNotFoundException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException
            or System.ComponentModel.Win32Exception
            or BadImageFormatException)
        {
            foreach (VulkanAppEntry entry in owned)
            {
                result.PreservedFiles.Add(entry.Path);
            }

            state.OwnedVulkanApps.RemoveAll(entry => SameUnregisterPath(entry.Executable, game.Executable));
            return;
        }

        foreach (string path in owned.Select(entry => entry.Path).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IniDocument? document;
            string[] applications;
            string hash;
            try
            {
                if (!Path.IsPathFullyQualified(path)
                    || !registeredLists.Any(registered => SameUnregisterPath(path, registered)))
                {
                    result.PreservedFiles.Add(path);
                    continue;
                }

                LocalFiles.CheckParents(path);
                if (LocalFiles.Link(path) != null || Directory.Exists(path))
                {
                    result.PreservedFiles.Add(path);
                    continue;
                }

                if (!File.Exists(path))
                {
                    continue;
                }

                byte[] contents = File.ReadAllBytes(path);
                hash = Convert.ToHexString(SHA256.HashData(contents));
                using var reader = new StreamReader(new MemoryStream(contents));
                document = new IniDocument(reader.ReadToEnd());
                applications = IniDocument.Split(document.Get("", "Apps"));
            }
            catch (Exception error) when (error is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or System.Security.SecurityException)
            {
                result.PreservedFiles.Add(path);
                continue;
            }

            string[] remaining = applications.Where(application =>
                !SameUnregisterPath(application, game.Executable))
                .ToArray();
            if (remaining.Length != applications.Length)
            {
                document.Set("", "Apps", IniDocument.Join(remaining));
                if (!transaction.RewriteIfUnchanged(path, hash, document.ToString()))
                {
                    result.PreservedFiles.Add(path);
                }
            }
        }

        state.OwnedVulkanApps.RemoveAll(entry => SameUnregisterPath(entry.Executable, game.Executable));
    }

    private static bool SameUnregisterPath(string? first, string? second)
    {
        try
        {
            return first != null
                && second != null
                && Path.IsPathFullyQualified(first)
                && Path.IsPathFullyQualified(second)
                && LocalFiles.Same(first, second);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
