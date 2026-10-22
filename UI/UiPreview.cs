using System.Drawing.Imaging;

namespace ReShadeCentralized;

internal static class UiPreview
{
    public static void Run(string[] args)
    {
        using var main = new MainForm(AppContext.BaseDirectory, preview: true)
        {
            Opacity = 0,
            ShowInTaskbar = false
        };
        main.Show();
        Application.DoEvents();
        VerifyPackageSelection(main);

        string? screenshot = Argument(args, "--screenshot");
        if (screenshot != null)
        {
            Capture(main, screenshot);
        }

        string? gallery = Argument(args, "--ui-gallery");
        if (gallery == null)
        {
            return;
        }

        Directory.CreateDirectory(gallery);
        TabControl tabs = Descendants(main).OfType<TabControl>().Single();
        for (var index = 0; index < tabs.TabCount; index++)
        {
            tabs.SelectedIndex = index;
            Capture(main, Path.Combine(gallery, $"page-{index + 1}.png"));
        }

        CaptureSettingsFooter(main, tabs, Path.Combine(gallery, "settings-footer.png"));

        Size originalSize = main.Size;
        main.Size = main.MinimumSize;
        for (var index = 0; index < tabs.TabCount; index++)
        {
            tabs.SelectedIndex = index;
            Capture(main, Path.Combine(gallery, $"compact-{index + 1}.png"));
        }

        CaptureSettingsFooter(main, tabs, Path.Combine(gallery, "compact-settings-footer.png"));

        main.Size = originalSize;
        tabs.SelectedIndex = 0;
        DataGridView grid = Descendants(main).OfType<DataGridView>().Single();
        grid.DataSource = new[]
        {
            new
            {
                Id = "preview",
                Name = "Example game",
                API = "DXGI",
                Build = "Standard",
                Architecture = "X64",
                Executable = @"C:\Games\Example\game.exe"
            }
        };
        Capture(main, Path.Combine(gallery, "game-selection.png"));

        using var setup = new SetupDialog(Path.Combine(gallery, "empty"));
        CaptureDialog(setup, Path.Combine(gallery, "setup.png"));
        using var game = new GameDialog(RuntimeBuild.Standard);
        CaptureDialog(game, Path.Combine(gallery, "install.png"));
        ComboBox api = Descendants(game).OfType<ComboBox>()
            .Single(combo => combo.Items.Contains(RenderApi.Vulkan));
        api.SelectedItem = RenderApi.Vulkan;
        ComboBox build = Descendants(game).OfType<ComboBox>()
            .Single(combo => combo.Items.Contains(RuntimeBuild.Standard));
        if (build.Enabled || !Equals(build.SelectedItem, RuntimeBuild.Standard))
        {
            throw new InvalidOperationException("The Vulkan dialog did not lock the global build selection.");
        }

        Capture(game, Path.Combine(gallery, "install-vulkan.png"));
        using var backup = new BackupDialog(Path.Combine(gallery, "empty"));
        CaptureDialog(backup, Path.Combine(gallery, "restore.png"));
    }

    private static string? Argument(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static void CaptureSettingsFooter(MainForm main, TabControl tabs, string path)
    {
        tabs.SelectedIndex = 2;
        Panel viewport = Descendants(tabs.SelectedTab!).OfType<Panel>().Single(panel => panel.AutoScroll);
        viewport.AutoScrollPosition = new Point(0, viewport.DisplayRectangle.Height);
        Capture(main, path);
        viewport.AutoScrollPosition = Point.Empty;
    }

    private static void VerifyPackageSelection(MainForm main)
    {
        TextBox search = Descendants(main).OfType<TextBox>()
            .Single(control => control.AccessibleName == "Search shader packages");
        CheckedListBox packages = Descendants(main).OfType<CheckedListBox>().Single();
        int initialCount = packages.Items.Count;
        search.Text = "__no_matching_package__";
        if (packages.Items.Count != 0)
        {
            throw new InvalidOperationException("Package filtering did not update the themed list.");
        }

        search.Clear();
        if (initialCount == 0 || packages.Items.Count != initialCount)
        {
            throw new InvalidOperationException("Clearing package search did not restore the catalog.");
        }

        int required = packages.Items.Cast<ShaderPackage>().ToList().FindIndex(package => package.Required);
        if (required < 0)
        {
            throw new InvalidOperationException("The preview catalog has no required package.");
        }

        packages.SetItemChecked(required, false);
        if (!packages.GetItemChecked(required))
        {
            throw new InvalidOperationException("A required package was deselected.");
        }
    }

    private static void CaptureDialog(Form form, string path)
    {
        form.Opacity = 0;
        form.ShowInTaskbar = false;
        form.Show();
        Capture(form, path);
    }

    private static void Capture(Form form, string path)
    {
        form.PerformLayout();
        form.Refresh();
        Application.DoEvents();
        VerifyCommandLayout(form);
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(path, ImageFormat.Png);
    }

    private static void VerifyCommandLayout(Form form)
    {
        foreach (FlowLayoutPanel bar in Descendants(form).OfType<FlowLayoutPanel>()
            .Where(panel => panel.Visible && !panel.WrapContents))
        {
            foreach (Control control in bar.Controls)
            {
                if (control.Left < 0 || control.Right + control.Margin.Right > bar.ClientSize.Width
                    || control.Bottom + control.Margin.Bottom > bar.ClientSize.Height)
                {
                    throw new InvalidOperationException($"The command '{control.Text}' is clipped in '{form.Text}'.");
                }
            }
        }
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            yield return control;
            foreach (Control descendant in Descendants(control))
            {
                yield return descendant;
            }
        }
    }
}
