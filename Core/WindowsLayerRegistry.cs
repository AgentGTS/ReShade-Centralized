using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ReShadeCentralized;

public sealed record LayerRegistration(string Hive, string View, string Manifest, string Kind = "Implicit");
public sealed record RegistrySnapshot(LayerRegistration Registration, int? PreviousValue);
public interface ILayerRegistry
{
    IEnumerable<LayerRegistration> Enumerate();

    int? Read(LayerRegistration key);

    void Write(LayerRegistration key, int? value);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsLayerRegistry(string registryRoot = @"SOFTWARE\Khronos\Vulkan") : ILayerRegistry
{
    private string KeyPath(LayerRegistration item) => registryRoot + "\\" + item.Kind + "Layers";

    private static RegistryKey OpenBase(LayerRegistration item) =>
        RegistryKey.OpenBaseKey(
        item.Hive == "HKLM" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
        item.View == "32" ? RegistryView.Registry32 : RegistryView.Registry64);

    public IEnumerable<LayerRegistration> Enumerate()
    {
        string[] views = Environment.Is64BitOperatingSystem ? ["64", "32"] : ["32"];
        foreach (string hive in new[] { "HKLM", "HKCU" })
        {
            foreach (string view in views)
            {
                foreach (string kind in new[] { "Implicit", "Explicit" })
                {
                    using RegistryKey root = OpenBase(new(hive, view, ""));
                    using RegistryKey? key = root.OpenSubKey(registryRoot + "\\" + kind + "Layers");
                    foreach (string name in key?.GetValueNames() ?? [])
                    {
                        yield return new(hive, view, name, kind);
                    }
                }
            }
        }
    }

    public int? Read(LayerRegistration item)
    {
        using RegistryKey root = OpenBase(item);
        using RegistryKey? key = root.OpenSubKey(KeyPath(item));
        object? value = key?.GetValue(item.Manifest);
        if (value == null)
        {
            return null;
        }

        if (value is not int number)
        {
            throw new InvalidDataException("Vulkan registration is not a DWORD: " + item.Manifest);
        }

        return number;
    }

    public void Write(LayerRegistration item, int? value)
    {
        using RegistryKey root = OpenBase(item);
        if (value == null)
        {
            using RegistryKey? existing = root.OpenSubKey(KeyPath(item), true);
            existing?.DeleteValue(item.Manifest, false);
        }
        else
        {
            using RegistryKey key = root.CreateSubKey(KeyPath(item), true);
            key.SetValue(item.Manifest, value.Value, RegistryValueKind.DWord);
        }
    }
}

