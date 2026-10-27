using System.Reflection;

namespace ReShadeCentralized;

internal static partial class UiPreview
{
    private static void VerifyCredits(MainForm main, string gallery)
    {
        TabControl tabs = Descendants(main).OfType<TabControl>().Single();
        TabPage credits = tabs.TabPages.Cast<TabPage>().Single(page => page.Text == "Credits");
        tabs.SelectedTab = credits;
        ListBox sources = Descendants(credits).OfType<ListBox>().Single();
        TextBox text = Descendants(credits).OfType<TextBox>().Single();
        foreach ((string source, string expected) in new[]
        {
            ("ReShade Centralized", "Copyright (c) 2020 OopyDoopy"),
            ("ReShade", "Copyright 2014 Patrick Mours"),
            ("Catalogs and shaders", "Attribution and runtime downloading do not create a license grant."),
            (".NET and dependencies", "Copyright (c) .NET Foundation and Contributors")
        })
        {
            sources.SelectedIndex = sources.Items.Cast<object>().ToList()
                .FindIndex(item => item.ToString() == source);
            if (!text.ReadOnly || !text.Text.Contains(expected))
            {
                throw new InvalidOperationException("The Credits tab is missing the complete notice for " + source);
            }
        }
        if (typeof(MainForm).Assembly.GetManifestResourceNames().Contains("ReShadeCentralized.ReleaseNotices.txt") &&
            !text.Text.Contains("Microsoft.WindowsDesktop.App.Runtime.win-x64"))
        {
            throw new InvalidOperationException("Published Credits must include the bundled desktop runtime notices.");
        }
        sources.SelectedIndex = 1;
        Capture(main, Path.Combine(gallery, "credits-reshade.png"));

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var manager = (Manager)typeof(MainForm).GetField("manager", flags)!.GetValue(main)!;
        PropertyInfo stateProperty = typeof(Manager).GetProperty(nameof(Manager.State))!;
        ManagerState? original = manager.State;
        string root = Path.Combine(gallery, "credits-fixture");
        CentralPaths paths = CentralPaths.At(root);
        string notice = Path.Combine(paths.Shaders, "fixture", "_notices", "LICENSE");
        Directory.CreateDirectory(Path.GetDirectoryName(notice)!);
        File.WriteAllText(notice, "Complete fixture license\nCopyright Fixture Author\n");
        var state = new ManagerState { Paths = paths };
        state.Packages.Add(new InstalledPackage
        {
            Id = "fixture",
            Name = "Fixture shader",
            Repository = "https://example.test/fixture",
            Files = new() { ["shaders/_notices/LICENSE"] = FileTransaction.Hash(notice) }
        });
        try
        {
            stateProperty.SetValue(manager, state);
            tabs.SelectedIndex = 0;
            tabs.SelectedTab = credits;
            sources.SelectedIndex = sources.Items.Count - 1;
            if (!text.Text.Contains("Copyright Fixture Author"))
            {
                throw new InvalidOperationException("Installed shader notices must be readable in Credits.");
            }
            Capture(main, Path.Combine(gallery, "credits-shader-notice.png"));
            File.Delete(notice);
            sources.SelectedIndex = 0;
            sources.SelectedIndex = sources.Items.Count - 1;
            if (!text.Text.StartsWith("Cannot read this notice:"))
            {
                throw new InvalidOperationException("Missing notice documents must not crash the Credits tab.");
            }
        }
        finally
        {
            stateProperty.SetValue(manager, original);
            tabs.SelectedIndex = 0;
        }
    }
}
