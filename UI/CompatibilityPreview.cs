namespace ReShadeCentralized;

internal static partial class UiPreview
{
    private static void VerifyCompatibilityReview(string gallery)
    {
        string cache = Path.Combine(gallery, CompatibilityCatalog.CacheName);
        File.WriteAllText(cache, "[bms.exe]\nRenderApi=D3D9\nInstallTarget=bin\n" +
            "[Dead Space.exe]\nRenderApi=D3D9\n");
        CompatibilityCatalog.LoadCache(cache);
        VerifyCompatibilityApiChoices(gallery);
        string root = Path.Combine(gallery, "compatibility-review");
        Directory.CreateDirectory(Path.Combine(root, ".trex"));
        string selected = Path.Combine(root, "game.exe");
        string bridge = Path.Combine(root, ".trex", "NvRemixBridge.exe");
        CreatePreviewExecutable(selected);
        CreatePreviewExecutable(bridge);
        using (var dialog = new GameDialog(RuntimeBuild.FullAddon) { Opacity = 0, ShowInTaskbar = false })
        {
            dialog.Show();
            Descendants(dialog).OfType<TextBox>()
                .Single(control => control.AccessibleName == "Game executable").Text = selected;
            ComboBox api = Descendants(dialog).OfType<ComboBox>()
                .Single(control => control.Items.Contains(RenderApi.Vulkan));
            ComboBox build = Descendants(dialog).OfType<ComboBox>()
                .Single(control => control.Items.Contains(RuntimeBuild.Standard));
            api.SelectedItem = RenderApi.OpenGL;
            build.SelectedItem = RuntimeBuild.Standard;
            Label changes = Descendants(dialog).OfType<Label>()
                .Single(control => control.AccessibleName == "Installation changes");
            if (dialog.Result().Executable != bridge || !changes.Text.Contains(bridge) ||
                dialog.Result().Api != RenderApi.OpenGL)
            {
                throw new InvalidOperationException(
                    "The review must show the resolved bridge and preserve a manual API.");
            }
            ClickApiCheck(dialog);
            LinkLabel hint = Descendants(dialog).OfType<LinkLabel>().Single();
            WaitForDetection(() => hint.Text.Contains("Recommended API"));
            if (!hint.Text.Contains("Vulkan") || !Equals(api.SelectedItem, RenderApi.OpenGL))
            {
                throw new InvalidOperationException("RTX Remix recommendations must preserve explicit API choices.");
            }
            api.SelectedItem = RenderApi.Vulkan;
            if (dialog.Result().Build != RuntimeBuild.FullAddon || build.Enabled)
            {
                throw new InvalidOperationException("Resolved Vulkan targets must follow the shared Vulkan build.");
            }
            Capture(dialog, Path.Combine(gallery, "install-remix-target.png"));
        }

        string source = Path.Combine(gallery, "compatibility-subfolder");
        string bin = Path.Combine(source, "bin");
        Directory.CreateDirectory(bin);
        selected = Path.Combine(source, "bms.exe");
        CreatePreviewExecutable(selected);
        using var subfolder = new GameDialog(RuntimeBuild.Standard) { Opacity = 0, ShowInTaskbar = false };
        subfolder.Show();
        Descendants(subfolder).OfType<TextBox>()
            .Single(control => control.AccessibleName == "Game executable").Text = selected;
        Descendants(subfolder).OfType<ComboBox>().Single(control => control.Items.Contains(RenderApi.Vulkan))
            .SelectedItem = RenderApi.Direct3D9;
        Descendants(subfolder).OfType<ComboBox>().Single(control => control.Items.Contains(RuntimeBuild.Standard))
            .SelectedItem = RuntimeBuild.Standard;
        Label review = Descendants(subfolder).OfType<Label>()
            .Single(control => control.AccessibleName == "Installation changes");
        if (!review.Text.Contains(bin) || !review.Text.Contains("bin\\d3d9.dll") ||
            subfolder.ReviewedPlan!.Links.Count != 3)
        {
            throw new InvalidOperationException("The native subfolder and both configuration links must be reviewed.");
        }
        Capture(subfolder, Path.Combine(gallery, "install-compatibility-subfolder.png"));
        Directory.Delete(bin);
        Descendants(subfolder).OfType<ComboBox>().Single(control => control.Items.Contains(RenderApi.Vulkan))
            .SelectedItem = RenderApi.DXGI;
        if (((Button)subfolder.AcceptButton!).Enabled ||
            !review.Text.Contains("compatibility install folder is missing"))
        {
            throw new InvalidOperationException("A missing compatibility folder must have an actionable review error.");
        }
    }

    private static void VerifyCompatibilityApiChoices(string gallery)
    {
        string root = Path.Combine(gallery, "compatibility-api-choice");
        Directory.CreateDirectory(root);
        string executable = Path.Combine(root, "Dead Space.exe");
        CreatePreviewExecutable(executable);
        using (var dialog = new GameDialog(RuntimeBuild.Standard) { Opacity = 0, ShowInTaskbar = false })
        {
            dialog.Show();
            Descendants(dialog).OfType<TextBox>()
                .Single(control => control.AccessibleName == "Game executable").Text = executable;
            ComboBox api = Descendants(dialog).OfType<ComboBox>()
                .Single(control => control.Items.Contains(RenderApi.Vulkan));
            ComboBox build = Descendants(dialog).OfType<ComboBox>()
                .Single(control => control.Items.Contains(RuntimeBuild.Standard));
            ClickApiCheck(dialog);
            LinkLabel hint = Descendants(dialog).OfType<LinkLabel>().Single();
            WaitForDetection(() => hint.Text.Contains("Recommended API"));
            if (!hint.Text.Contains(DisplayNames.For(RenderApi.Direct3D9)) ||
                api.SelectedIndex != 0 || build.SelectedIndex != 0 || ((Button)dialog.AcceptButton!).Enabled)
            {
                throw new InvalidOperationException("Catalog fallback must not select API or build for a new game.");
            }
            Capture(dialog, Path.Combine(gallery, "install-catalog-recommendation.png"));
        }

        var profile = new GameProfile
        {
            Executable = executable,
            Api = RenderApi.DXGI,
            Build = RuntimeBuild.FullAddon
        };
        using var saved = new GameDialog(RuntimeBuild.Standard, profile) { Opacity = 0, ShowInTaskbar = false };
        saved.Show();
        ClickApiCheck(saved);
        WaitForDetection(() => Descendants(saved).OfType<LinkLabel>().Single().Text.Contains("Recommended API"));
        if (saved.Result().Api != profile.Api || saved.Result().Build != profile.Build)
        {
            throw new InvalidOperationException("Editing a profile must load and preserve its saved API and build.");
        }
    }
}
