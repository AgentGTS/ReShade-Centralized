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
        VerifyButtonStates(main, false, "Repair / edit...", "Remove", "Open profile", "Open game folder", "Repair all",
            "Update installed packages", "Manage game...");

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
                Name = "game.exe",
                API = DisplayNames.For(RenderApi.DXGI),
                Build = DisplayNames.For(RuntimeBuild.FullAddon),
                Architecture = "X64",
                Executable = @"C:\Games\Example\game.exe"
            }
        };
        Application.DoEvents();
        VerifyButtonStates(main, true, "Repair / edit...", "Remove", "Open profile", "Open game folder", "Repair all");
        grid.ClearSelection();
        VerifyButtonStates(main, false, "Repair / edit...", "Remove", "Open profile", "Open game folder");
        VerifyButtonStates(main, true, "Repair all");
        grid.Rows[0].Selected = true;
        VerifyButtonStates(main, true, "Repair / edit...", "Remove", "Open profile", "Open game folder");
        Capture(main, Path.Combine(gallery, "game-selection.png"));
        main.Size = main.MinimumSize;
        Capture(main, Path.Combine(gallery, "compact-game-selection.png"));
        main.Size = originalSize;
        VerifyVulkanSelection(main);
        tabs.SelectedIndex = 2;
        VulkanGamesList consumers = Descendants(main).OfType<VulkanGamesList>().Single();
        consumers.Parent!.Visible = true;
        consumers.SetEntries([
            new VulkanGameEntry(@"C:\Games\Example\game.exe", true),
            new VulkanGameEntry(@"C:\Games\Other\other.exe", false)
        ]);
        consumers.SelectedIndex = 0;
        VerifyButtonStates(main, false, "Manage game...");
        Capture(main, Path.Combine(gallery, "vulkan-consumers.png"));
        main.Size = main.MinimumSize;
        Capture(main, Path.Combine(gallery, "compact-vulkan-consumers.png"));
        main.Size = originalSize;
        consumers.Items.Clear();
        consumers.Parent.Visible = false;

        using var setup = new SetupDialog(Path.Combine(gallery, "empty"));
        CaptureDialog(setup, Path.Combine(gallery, "setup.png"));
        using var game = new GameDialog(RuntimeBuild.Standard);
        ComboBox api = Descendants(game).OfType<ComboBox>()
            .Single(combo => combo.Items.Contains(RenderApi.Vulkan));
        ComboBox build = Descendants(game).OfType<ComboBox>()
            .Single(combo => combo.Items.Contains(RuntimeBuild.Standard));
        if (api.GetItemText(RenderApi.Direct3D9) != "DirectX 9")
        {
            throw new InvalidOperationException("The DirectX 9 selection label is incorrect.");
        }

        api.SelectedItem = RenderApi.DXGI;
        build.SelectedItem = RuntimeBuild.FullAddon;
        if (api.GetItemText(api.SelectedItem) != "DXGI (DirectX 10/11/12)" ||
            build.GetItemText(build.SelectedItem) != "Standard + add-on support" ||
            game.Result().Api != RenderApi.DXGI || game.Result().Build != RuntimeBuild.FullAddon)
        {
            throw new InvalidOperationException("API/build labels changed their underlying selection values.");
        }

        CaptureDialog(game, Path.Combine(gallery, "install.png"));
        if (((Button)game.AcceptButton!).Enabled)
        {
            throw new InvalidOperationException("Install is enabled before selecting an executable.");
        }

        api.SelectedItem = RenderApi.Vulkan;
        if (build.Enabled || !Equals(build.SelectedItem, RuntimeBuild.Standard))
        {
            throw new InvalidOperationException("The Vulkan dialog did not lock the global build selection.");
        }

        Capture(game, Path.Combine(gallery, "install-vulkan.png"));
        VerifyExistingInstallationNotice(gallery);
        using var backup = new BackupDialog(Path.Combine(gallery, "empty"));
        CaptureDialog(backup, Path.Combine(gallery, "restore.png"));
        VerifyButtonStates(backup, false, "Restore before operation");
        VerifyRestoreSelection(gallery);
        using var help = new HelpDialog();
        CaptureDialog(help, Path.Combine(gallery, "help.png"));
        help.Size = help.MinimumSize;
        Capture(help, Path.Combine(gallery, "compact-help.png"));
    }

    private static void VerifyButtonStates(Control parent, bool enabled, params string[] names)
    {
        foreach (string name in names)
        {
            Button button = Descendants(parent).OfType<Button>().Single(control => control.Text == name);
            if (button.Enabled != enabled)
            {
                throw new InvalidOperationException($"The enabled state of '{name}' does not match its selection.");
            }
        }
    }

    private static void VerifyVulkanSelection(MainForm main)
    {
        VulkanGamesList list = Descendants(main).OfType<VulkanGamesList>().Single();
        list.Parent!.Visible = true;
        list.SetEntries([
            new VulkanGameEntry(@"C:\Games\Example\game.exe", false),
            new VulkanGameEntry(@"C:\Games\Managed\managed.exe", true)
        ]);
        VerifyButtonStates(main, false, "Manage game...");
        list.SelectedIndex = 0;
        VerifyButtonStates(main, true, "Manage game...");
        list.SelectedIndex = 1;
        VerifyButtonStates(main, false, "Manage game...");
        list.SelectedIndex = 0;
        list.SetEntries([new VulkanGameEntry(@"C:\Games\Example\game.exe", true)]);
        VerifyButtonStates(main, false, "Manage game...");
        list.ClearSelected();
        VerifyButtonStates(main, false, "Manage game...");
        list.Items.Clear();
        list.Parent.Visible = false;
    }

    private static void VerifyRestoreSelection(string gallery)
    {
        string backups = Path.Combine(gallery, "restore-fixture");
        using (var transaction = new FileTransaction(backups, "Preview operation"))
        {
            transaction.WriteText(Path.Combine(gallery, "restore-marker.txt"), "UI preview fixture");
            transaction.Commit();
        }

        using var dialog = new BackupDialog(backups);
        dialog.ShowInTaskbar = false;
        dialog.Opacity = 0;
        dialog.Show();
        VerifyButtonStates(dialog, true, "Restore before operation");
        ListBox list = Descendants(dialog).OfType<ListBox>().Single();
        list.ClearSelected();
        VerifyButtonStates(dialog, false, "Restore before operation");
        list.SelectedIndex = 0;
        VerifyButtonStates(dialog, true, "Restore before operation");
    }

    private static void VerifyExistingInstallationNotice(string gallery)
    {
        string directory = Path.Combine(gallery, "unmanaged-install");
        Directory.CreateDirectory(Path.Combine(directory, "reshade-shaders"));
        string path = Path.Combine(directory, "game.exe");
        File.WriteAllText(path, "UI preview fixture");
        File.WriteAllText(Path.Combine(directory, "ReShade.ini"), "[GENERAL]\nPerformanceMode=1\n");
        using var dialog = new GameDialog(RuntimeBuild.Standard);
        dialog.Opacity = 0;
        dialog.ShowInTaskbar = false;
        dialog.Show();
        TextBox executable = Descendants(dialog).OfType<TextBox>()
            .Single(control => control.AccessibleName == "Game executable");
        Label warning = Descendants(dialog).OfType<Label>()
            .Single(control => control.AccessibleName == "Existing ReShade installation");
        int originalHeight = dialog.ClientSize.Height;
        executable.Text = path;
        if (!warning.Visible || !warning.Text.Contains("reshade-shaders") ||
            dialog.ClientSize.Height <= originalHeight)
        {
            throw new InvalidOperationException("The unmanaged installation warning did not appear below the executable.");
        }

        if (dialog.Result().Name != "game.exe")
        {
            throw new InvalidOperationException("The game name did not match the executable filename.");
        }

        Capture(dialog, Path.Combine(gallery, "install-unmanaged.png"));
        ComboBox api = Descendants(dialog).OfType<ComboBox>()
            .Single(control => control.Items.Contains(RenderApi.DXGI));
        api.SelectedItem = RenderApi.DXGI;
        File.WriteAllText(Path.Combine(directory, "dxgi.dll"), "Unrelated DLL preview fixture");
        executable.Text = "";
        executable.Text = path;
        if (((Button)dialog.AcceptButton!).Enabled || !warning.Text.Contains("unrecognized DLL"))
        {
            throw new InvalidOperationException("An unrelated DLL conflict did not disable installation.");
        }

        Capture(dialog, Path.Combine(gallery, "install-conflict.png"));
        executable.Text = Path.Combine(directory, "missing.exe");
        if (warning.Visible || dialog.ClientSize.Height != originalHeight || dialog.Result().Name != "missing.exe")
        {
            throw new InvalidOperationException("The installation warning did not clear after changing the executable.");
        }
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

        VerifyButtonStates(main, true, "Install / update selected");

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
        if (form is MainForm)
        {
            ComboBox selector = Descendants(form).OfType<ComboBox>()
                .Single(control => control.AccessibleName == "Global Vulkan build");
            Button apply = Descendants(form).OfType<Button>().Single(control => control.Text == "Apply / repair");
            if (selector.Height != apply.Height || selector.Top != apply.Top)
            {
                throw new InvalidOperationException("The Vulkan selector and Apply / repair button are misaligned.");
            }

            Button help = Descendants(form).OfType<Button>().Single(control => control.AccessibleName == "Help");
            Button cancel = Descendants(help.Parent!).OfType<Button>().Single(control => control.Text == "Cancel");
            if (help.Height != cancel.Height || help.Top != cancel.Top)
            {
                throw new InvalidOperationException("The footer Help and Cancel buttons are misaligned.");
            }
        }

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
