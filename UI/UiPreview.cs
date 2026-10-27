using System.Drawing.Imaging;

namespace ReShadeCentralized;

internal static partial class UiPreview
{
    public static void Run(string[] args)
    {
        Control.CheckForIllegalCrossThreadCalls = true;
        Exception? failure = null;
        using var host = new Form { Opacity = 0, ShowInTaskbar = false };
        host.Shown += (_, _) => host.BeginInvoke(() =>
        {
            try
            {
                RunChecks(args);
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                host.Close();
            }
        });
        Application.Run(host);
        if (failure != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void RunChecks(string[] args)
    {
        using var main = new MainForm(AppContext.BaseDirectory, preview: true)
        {
            Opacity = 0,
            ShowInTaskbar = false
        };
        main.Show();
        Application.DoEvents();
        VerifyPackageSelection(main);
        VerifyShaderSelectionModes(main);
        VerifyButtonStates(main, false, "Repair / edit...", "Unregister", "Open profile", "Open game folder", "Repair all",
            "Purge shader folder...", "Manage game...");

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

        gallery = Path.GetFullPath(gallery);
        Directory.CreateDirectory(gallery);
        VerifyShaderInventoryAndGridLabels(main, gallery);
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
        DataGridView grid = Descendants(main).OfType<GamesGrid>().Single();
        grid.DataSource = new[]
        {
            new
            {
                Id = "preview",
                InstallationWarning = "Managed link is missing: C:\\Games\\Example\\dxgi.dll\n\n" +
                    "Check the game folder or use Repair / edit.",
                Name = "game.exe",
                API = "DXGI",
                Build = "Standard + add-on",
                Architecture = "64-bit",
                Executable = @"C:\Games\Example\game.exe"
            }
        };
        Application.DoEvents();
        DataGridViewColumn warningColumn = grid.Columns["InstallationWarning"]!;
        string tooltip = grid.Rows[0].Cells[warningColumn.Index].ToolTipText;
        if (!tooltip.Contains("Repair / edit") || !tooltip.Contains("dxgi.dll") ||
            warningColumn.AutoSizeMode != DataGridViewAutoSizeColumnMode.None)
        {
            throw new InvalidOperationException("Game installation warnings need a compact icon and an actionable tooltip.");
        }

        VerifyButtonStates(main, true, "Repair / edit...", "Unregister", "Open profile", "Open game folder", "Repair all");
        grid.ClearSelection();
        VerifyButtonStates(main, false, "Repair / edit...", "Unregister", "Open profile", "Open game folder");
        VerifyButtonStates(main, true, "Repair all");
        grid.Rows[0].Selected = true;
        VerifyButtonStates(main, true, "Repair / edit...", "Unregister", "Open profile", "Open game folder");
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

        using var setup = new SetupDialog();
        if (setup.CentralRoot != StateStore.DefaultDirectory)
        {
            throw new InvalidOperationException("Setup must offer the default central storage folder.");
        }

        CaptureDialog(setup, Path.Combine(gallery, "setup.png"));
        using var game = new GameDialog(RuntimeBuild.Standard);
        ComboBox api = Descendants(game).OfType<ComboBox>()
            .Single(combo => combo.Items.Contains(RenderApi.Vulkan));
        ComboBox build = Descendants(game).OfType<ComboBox>()
            .Single(combo => combo.Items.Contains(RuntimeBuild.Standard));
        CaptureDialog(game, Path.Combine(gallery, "install.png"));
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

        Capture(game, Path.Combine(gallery, "install-selected.png"));
        if (((Button)game.AcceptButton!).Enabled)
        {
            throw new InvalidOperationException("Install is enabled before selecting an executable.");
        }

        api.SelectedItem = RenderApi.Vulkan;
        if (build.Enabled || !Equals(build.SelectedItem, RuntimeBuild.Standard) ||
            build.GetItemText(build.SelectedItem) != "Standard (Global)")
        {
            throw new InvalidOperationException("The Vulkan dialog must lock the global build selection.");
        }

        Capture(game, Path.Combine(gallery, "install-vulkan.png"));
        VerifyApiDetection(gallery);
        VerifyExistingInstallationNotice(gallery);
        VerifyAdaptiveInstallLayout(gallery);
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

    private static void VerifyExistingInstallationNotice(string gallery)
    {
        string directory = Path.Combine(gallery, "unmanaged-install");
        Directory.CreateDirectory(Path.Combine(directory, "reshade-shaders", "Shaders"));
        string path = Path.Combine(directory, "game.exe");
        CreatePreviewExecutable(path);
        File.WriteAllText(Path.Combine(directory, "ReShade.ini"), "[GENERAL]\nPerformanceMode=1\n");
        using var dialog = new GameDialog(RuntimeBuild.Standard);
        dialog.Opacity = 0;
        dialog.ShowInTaskbar = false;
        dialog.Show();
        TextBox executable = Descendants(dialog).OfType<TextBox>()
            .Single(control => control.AccessibleName == "Game executable");
        Label warning = Descendants(dialog).OfType<Label>()
            .Single(control => control.AccessibleName == "Installation changes");
        int originalHeight = dialog.ClientSize.Height;
        executable.Text = path;
        if (!warning.Visible || !warning.Text.Contains("reshade-shaders") ||
            dialog.ClientSize.Height <= originalHeight)
        {
            throw new InvalidOperationException("The unmanaged installation warning did not appear below the executable.");
        }

        ComboBox api = Descendants(dialog).OfType<ComboBox>()
            .Single(control => control.Items.Contains(RenderApi.DXGI));
        ComboBox build = Descendants(dialog).OfType<ComboBox>()
            .Single(control => control.Items.Contains(RuntimeBuild.Standard));
        api.SelectedItem = RenderApi.Direct3D9;
        build.SelectedItem = RuntimeBuild.Standard;
        CheckBox confirmation = Descendants(dialog).OfType<CheckBox>().Single();
        VerifyButtonStates(dialog, false, "Install");
        if (!confirmation.Visible || !warning.Text.Contains("Replaced files will be deleted") ||
            warning.Text.Contains("Game folder:") ||
            warning.Text.Contains(dialog.ReviewedPlan!.ProfileDirectory) || !warning.Text.Contains("game profile"))
        {
            throw new InvalidOperationException("Adoption must name its destinations without paths and require consent.");
        }

        confirmation.Checked = true;
        VerifyButtonStates(dialog, true, "Install");
        if (dialog.Result().Name != "game.exe")
        {
            throw new InvalidOperationException("The game name did not match the executable filename.");
        }

        Capture(dialog, Path.Combine(gallery, "install-unmanaged.png"));
        api.SelectedItem = RenderApi.DXGI;
        if (confirmation.Checked)
        {
            throw new InvalidOperationException("Changing the installation plan must clear replacement consent.");
        }
        File.WriteAllText(Path.Combine(directory, "dxgi.dll"), "Unrelated DLL preview fixture");
        executable.Text = "";
        executable.Text = path;
        if (((Button)dialog.AcceptButton!).Enabled || !warning.Text.Contains("unrecognized DLL"))
        {
            throw new InvalidOperationException("An unrelated DLL conflict did not disable installation.");
        }

        Capture(dialog, Path.Combine(gallery, "install-conflict.png"));
        int longWarningHeight = dialog.ClientSize.Height;
        File.Delete(Path.Combine(directory, "ReShade.ini"));
        executable.Text = "";
        executable.Text = path;
        Capture(dialog, Path.Combine(gallery, "install-short-warning.png"));
        if (dialog.ClientSize.Height > longWarningHeight)
        {
            throw new InvalidOperationException("A shorter installation warning must shrink the dialog.");
        }
        executable.Text = Path.Combine(directory, "missing.exe");
        if (warning.Visible || dialog.ClientSize.Height != originalHeight || dialog.Result().Name != "missing.exe")
        {
            throw new InvalidOperationException("The installation warning did not clear after changing the executable.");
        }
        Capture(dialog, Path.Combine(gallery, "install-warning-cleared.png"));

        string hook = Path.Combine(directory, "dxgi.dll");
        File.Delete(hook);
        File.CreateSymbolicLink(hook, Path.Combine(directory, "previous-runtime", "ReShade64.dll"));
        executable.Text = path;
        if (dialog.ReviewedPlan?.Conflict != null || !warning.Text.Contains("Replace link:") ||
            warning.Text.Contains("Game folder:") || !confirmation.Visible)
        {
            throw new InvalidOperationException("An existing ReShade link must allow replacement after review.");
        }
        confirmation.Checked = true;
        VerifyButtonStates(dialog, true, "Install");
        Capture(dialog, Path.Combine(gallery, "install-existing-symlink.png"));
    }

    private static string? Argument(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static void VerifyAdaptiveInstallLayout(string gallery)
    {
        foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f })
        {
            using var scaledFont = new Font(DarkTheme.BodyFont.FontFamily, DarkTheme.BodyFont.Size * scale);
            using var dialog = new GameDialog(RuntimeBuild.Standard) { Opacity = 0, ShowInTaskbar = false };
            dialog.Show();
            if (dialog.FormBorderStyle != FormBorderStyle.FixedDialog || dialog.MaximizeBox ||
                dialog.SizeGripStyle != SizeGripStyle.Hide || dialog.MinimumSize.IsEmpty)
            {
                throw new InvalidOperationException("The wizard must have a minimum size and disallow user resizing.");
            }
            if (scale != 1f)
            {
                dialog.Scale(new SizeF(scale, scale));
                dialog.Font = scaledFont;
            }

            Label notice = Descendants(dialog).OfType<Label>()
                .Single(control => control.AccessibleName == "Installation changes");
            notice.Text = "Existing ReShade files will be replaced in C:\\Games\\Example & Custom Shaders\\game. " +
                "Review these changes before installing.";
            notice.Parent!.Parent!.Visible = true;
            LinkLabel apiMessage = Descendants(dialog).OfType<LinkLabel>().Single();
            apiMessage.Text = "Could not identify the graphics API for this executable. " +
                "Select the graphics API used by the game below.";
            Descendants(dialog).OfType<CheckBox>().Single().Visible = true;
            System.Reflection.MethodInfo resize = typeof(GameDialog).GetMethod("UpdateDialogSize",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            resize.Invoke(dialog, null);
            Capture(dialog, Path.Combine(gallery, $"install-inline-{scale:0.##}.png"));

            notice.Text = string.Join("\n\n", Enumerable.Repeat(
                "Link: C:\\Games\\" + new string('x', 180) + "\\ReShade.ini\n" +
                "  -> C:\\Central\\Profiles\\Example_0123456789abcdef\\ReShade.ini", 40));
            resize.Invoke(dialog, null);
            var warningViewport = (Panel)notice.Parent;
            var contentViewport = (Panel)warningViewport.Parent!.Parent!.Parent!;
            int maxWarningHeight = (int)Math.Ceiling(notice.Font.GetHeight(dialog.DeviceDpi) * 10);
            if (!warningViewport.VerticalScroll.Visible || warningViewport.HorizontalScroll.Visible ||
                !notice.Visible || warningViewport.Height > maxWarningHeight || contentViewport.VerticalScroll.Visible ||
                contentViewport.HorizontalScroll.Visible)
            {
                throw new InvalidOperationException("Only the notice area may scroll, with at most ten visible lines.");
            }

            Capture(dialog, Path.Combine(gallery, $"install-overflow-{scale:0.##}.png"));
            notice.Text = "Short installation information.";
            resize.Invoke(dialog, null);
            if (warningViewport.VerticalScroll.Visible)
            {
                throw new InvalidOperationException("The install dialog must shrink when its information gets shorter.");
            }

            CheckBox confirmation = Descendants(dialog).OfType<CheckBox>().Single();
            foreach (string message in new[] { "", "Short installation information.",
                string.Join("\n\n", Enumerable.Repeat("Replace link: dxgi.dll -> central runtime (ReShade64.dll).", 20)) })
            {
                foreach (bool showApiMessage in new[] { false, true })
                {
                    foreach (bool showConfirmation in new[] { false, true, false, true })
                    {
                        notice.Text = message;
                        notice.Parent!.Parent!.Visible = message.Length > 0;
                        apiMessage.Parent!.Visible = showApiMessage;
                        confirmation.Visible = showConfirmation;
                        resize.Invoke(dialog, null);
                        Application.DoEvents();
                        TableLayoutPanel fields = Descendants(dialog).OfType<TableLayoutPanel>()
                            .Single(table => table.Controls.OfType<ComboBox>().Any());
                        VerifyInstallDialogSpacing(dialog, fields);
                    }
                }
            }
        }
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
        PackagesGrid packages = Descendants(main).OfType<PackagesGrid>().Single();
        int initialCount = packages.Rows.Count;
        search.Text = "__no_matching_package__";
        if (packages.Rows.Count != 0)
        {
            throw new InvalidOperationException("Package filtering did not update the themed list.");
        }

        VerifyButtonStates(main, true, "Apply selection");

        search.Clear();
        if (initialCount == 0 || packages.Rows.Count != initialCount)
        {
            throw new InvalidOperationException("Clearing package search did not restore the catalog.");
        }

        int required = packages.Rows.Cast<DataGridViewRow>().ToList()
            .FindIndex(row => row.Tag is ShaderPackage { Required: true });
        if (required < 0)
        {
            throw new InvalidOperationException("The preview catalog has no required package.");
        }

        packages.Rows[required].Cells["Selected"].Value = false;
        if (packages.Rows[required].Cells["Selected"].Value is not true ||
            !packages.Rows[required].Cells["Selected"].ReadOnly)
        {
            throw new InvalidOperationException("A required package was deselected.");
        }
    }

    private static void VerifyShaderSelectionModes(MainForm main)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic;
        var manager = (Manager)typeof(MainForm).GetField("manager", flags)!.GetValue(main)!;
        System.Reflection.PropertyInfo stateProperty = typeof(Manager).GetProperty(nameof(Manager.State))!;
        System.Reflection.MethodInfo filter = typeof(MainForm).GetMethod("FilterPackages", flags)!;
        var choices = (HashSet<string>)typeof(MainForm).GetField("selectedPackageIds", flags)!.GetValue(main)!;
        string[] originalChoices = choices.ToArray();
        ManagerState? originalState = manager.State;
        PackagesGrid grid = Descendants(main).OfType<PackagesGrid>().Single();
        DataGridViewColumn statusColumn = grid.Columns["Status"]!;
        int statusWidth = statusColumn.Width;
        if (statusColumn.AutoSizeMode != DataGridViewAutoSizeColumnMode.None ||
            statusColumn.Resizable != DataGridViewTriState.False ||
            statusWidth < TextRenderer.MeasureText("Incomplete (will be removed)", grid.Font).Width +
                grid.DefaultCellStyle.Padding.Horizontal)
        {
            throw new InvalidOperationException("The status column must be locked wide enough for its longest text.");
        }
        TabControl tabs = Descendants(main).OfType<TabControl>().Single();
        int originalTab = tabs.SelectedIndex;
        ShaderPackage[] packages = grid.Rows.Cast<DataGridViewRow>().Select(row => (ShaderPackage)row.Tag!).ToArray();
        ShaderPackage[] optional = packages.Where(package => !package.Required).Take(2).ToArray();
        var state = new ManagerState
        {
            Paths = CentralPaths.At(Path.Combine(AppContext.BaseDirectory, "selection-preview"))
        };
        state.Packages = packages.Where(package => package.Required || package.Id == optional[0].Id)
            .Select(package => new InstalledPackage { Id = package.Id, Name = package.Name }).ToList();
        TextBox search = Descendants(main).OfType<TextBox>()
            .Single(control => control.AccessibleName == "Search shader packages");

        DataGridViewRow PackageRow(string id) => grid.Rows.Cast<DataGridViewRow>()
            .Single(row => ((ShaderPackage)row.Tag!).Id == id);

        void VerifyStatus(string id, string text, Color color)
        {
            DataGridViewCell status = PackageRow(id).Cells["Status"];
            if (!Equals(status.Value, text) || status.Style.ForeColor != color ||
                status.Style.SelectionForeColor != color || statusColumn.Width != statusWidth)
            {
                throw new InvalidOperationException($"Unexpected selection status for {id}: {status.Value}.");
            }
        }

        try
        {
            stateProperty.SetValue(manager, state);
            choices.Clear();
            choices.UnionWith(state.Packages.Select(package => package.Id));
            filter.Invoke(main, null);
            VerifyButtonStates(main, true, "Update selected");
            VerifyStatus(optional[1].Id, "Not installed", Color.Empty);

            PackageRow(optional[1].Id).Cells["Selected"].Value = true;
            VerifyButtonStates(main, true, "Apply selection");
            VerifyStatus(optional[1].Id, "Not installed (will be added)", DarkTheme.Warning);
            PackageRow(optional[0].Id).Cells["Selected"].Value = false;
            VerifyStatus(optional[0].Id, "Incomplete (will be removed)", DarkTheme.Danger);
            search.Text = "__hidden_selection__";
            VerifyButtonStates(main, true, "Apply selection");
            search.Clear();
            VerifyStatus(optional[0].Id, "Incomplete (will be removed)", DarkTheme.Danger);
            VerifyStatus(optional[1].Id, "Not installed (will be added)", DarkTheme.Warning);

            PackageRow(optional[1].Id).Cells["Selected"].Value = false;
            VerifyStatus(optional[1].Id, "Not installed", Color.Empty);
            VerifyButtonStates(main, true, "Apply selection");
            PackageRow(optional[0].Id).Cells["Selected"].Value = true;
            VerifyStatus(optional[0].Id, "Incomplete", Color.Empty);
            VerifyButtonStates(main, true, "Update selected");
            search.Text = "__hidden_selection__";
            VerifyButtonStates(main, true, "Update selected");
            search.Clear();

            state.Packages.Add(new InstalledPackage { Id = "catalog-missing", Name = "Recorded package" });
            filter.Invoke(main, null);
            VerifyButtonStates(main, true, "Apply selection");
            VerifyStatus("catalog-missing", "Incomplete (will be removed)", DarkTheme.Danger);
            PackageRow("catalog-missing").Cells["Selected"].Value = true;
            VerifyButtonStates(main, true, "Update selected");

            state.SelectedPackages = choices.ToList();
            tabs.SelectedIndex = 1;
            PackageRow(optional[0].Id).Cells["Selected"].Value = false;
            PackageRow(optional[1].Id).Cells["Selected"].Value = true;
            tabs.SelectedIndex = 0;
            tabs.SelectedIndex = 1;
            if (!choices.SetEquals(state.SelectedPackages))
            {
                throw new InvalidOperationException("Leaving the shader tab must discard unapplied changes.");
            }
            VerifyStatus(optional[0].Id, "Incomplete", Color.Empty);
            VerifyStatus(optional[1].Id, "Not installed", Color.Empty);
            VerifyButtonStates(main, true, "Update selected");

            System.Reflection.MethodInfo reset = typeof(MainForm).GetMethod("ResetPackageChoices", flags)!;
            PackageRow(optional[0].Id).Cells["Selected"].Value = false;
            PackageRow(optional[1].Id).Cells["Selected"].Value = true;
            reset.Invoke(main, null);
            if (!choices.SetEquals(state.SelectedPackages))
            {
                throw new InvalidOperationException("Catalog/purge reset must restore the saved selection.");
            }
            state.Packages.Clear();
            reset.Invoke(main, null);
            if (!choices.SetEquals(state.SelectedPackages))
            {
                throw new InvalidOperationException("Purged inventory must retain the last applied reinstall choices.");
            }
            VerifyStatus(optional[0].Id, "Not installed (will be added)", DarkTheme.Warning);
            VerifyButtonStates(main, true, "Apply selection");

            Font originalFont = grid.Font;
            using var largerFont = new Font(originalFont.FontFamily, originalFont.Size + 3);
            grid.Font = largerFont;
            if (statusColumn.Width <= statusWidth)
            {
                throw new InvalidOperationException("The fixed status width must adapt to a larger font.");
            }
            grid.Font = originalFont;
            if (statusColumn.Width != statusWidth)
            {
                throw new InvalidOperationException("Restoring the font must restore the original status width.");
            }
        }
        finally
        {
            tabs.SelectedIndex = originalTab;
            stateProperty.SetValue(manager, originalState);
            choices.Clear();
            choices.UnionWith(originalChoices);
            search.Clear();
            filter.Invoke(main, null);
        }
    }

    private static void VerifyShaderInventoryAndGridLabels(MainForm main, string gallery)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic;
        var manager = (Manager)typeof(MainForm).GetField("manager", flags)!.GetValue(main)!;
        System.Reflection.PropertyInfo stateProperty = typeof(Manager).GetProperty(nameof(Manager.State))!;
        System.Reflection.MethodInfo refresh = typeof(MainForm).GetMethod("RefreshView", flags)!;
        System.Reflection.MethodInfo synchronize = typeof(MainForm).GetMethod("SynchronizePackageChoices", flags)!;
        var choices = (HashSet<string>)typeof(MainForm).GetField("selectedPackageIds", flags)!.GetValue(main)!;
        string[] originalChoices = choices.ToArray();
        var state = new ManagerState { Paths = CentralPaths.At(Path.Combine(gallery, "shader-state")) };
        var packages = Descendants(main).OfType<PackagesGrid>().Single();
        ShaderPackage optional = packages.Rows.Cast<DataGridViewRow>().Select(row => (ShaderPackage)row.Tag!)
            .First(package => !package.Required);
        string file = Path.Combine(state.Paths.Shaders, optional.Id, "Test.fx");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "UI fixture");
        state.Packages.Add(new InstalledPackage { Id = optional.Id, Name = optional.Name,
            Files = new() { ["shaders/Test.fx"] = FileTransaction.Hash(file) } });
        state.Packages.Add(new InstalledPackage { Id = "missing-from-catalog", Name = "Recorded package" });
        state.SelectedPackages = [optional.Id, "missing-from-catalog"];
        state.Games.Add(new GameProfile { Executable = @"C:\Example\game.exe", Api = RenderApi.DXGI,
            Build = RuntimeBuild.FullAddon, Architecture = CpuArchitecture.X64 });
        state.Games.Add(new GameProfile { Executable = @"C:\Example32\game.exe", Architecture = CpuArchitecture.X86 });
        TextBox search = Descendants(main).OfType<TextBox>()
            .Single(control => control.AccessibleName == "Search shader packages");
        try
        {
            stateProperty.SetValue(manager, state);
            synchronize.Invoke(main, null);
            refresh.Invoke(main, null);
            DataGridView games = Descendants(main).OfType<GamesGrid>().Single();
            if (!Equals(games.Rows[0].Cells["API"].Value, "DXGI") ||
                !Equals(games.Rows[0].Cells["Build"].Value, "Standard + add-on") ||
                !Equals(games.Rows[0].Cells["Architecture"].Value, "64-bit") ||
                !Equals(games.Rows[1].Cells["Architecture"].Value, "32-bit"))
            {
                throw new InvalidOperationException("Main game grid labels do not match their display contract.");
            }
            DataGridViewRow installed = packages.Rows.Cast<DataGridViewRow>()
                .Single(row => ((ShaderPackage)row.Tag!).Id == optional.Id);
            if (!Equals(installed.Cells["Status"].Value, "Installed") || installed.Cells["Selected"].Value is not true)
            {
                throw new InvalidOperationException("Installed shaders must show their checked and installed states separately.");
            }
            VerifyButtonStates(main, false, "Remove app-owned layers...");
            state.OwnedVulkanRegistrations.Add(new("HKLM", "64",
                Path.Combine(state.Paths.Root, "Vulkan", "ReShade64.json")));
            refresh.Invoke(main, null);
            VerifyButtonStates(main, true, "Remove app-owned layers...");
            state.Games[0].Api = RenderApi.Vulkan;
            refresh.Invoke(main, null);
            VerifyButtonStates(main, false, "Remove app-owned layers...");
            state.Games[0].Api = RenderApi.DXGI;
            state.OwnedVulkanRegistrations.Clear();
            refresh.Invoke(main, null);
            installed = packages.Rows.Cast<DataGridViewRow>().Single(row => ((ShaderPackage)row.Tag!).Id == optional.Id);
            installed.Cells["Selected"].Value = false;
            if (!Equals(installed.Cells["Status"].Value, "Installed (will be removed)") ||
                installed.Cells["Status"].Style.ForeColor != DarkTheme.Danger ||
                installed.Cells["Status"].Style.SelectionForeColor != DarkTheme.Danger)
            {
                throw new InvalidOperationException("Unchecked installed packages must show a red removal warning.");
            }
            installed.Cells["Selected"].Value = true;
            if (!Equals(installed.Cells["Status"].Value, "Installed") ||
                !installed.Cells["Status"].Style.ForeColor.IsEmpty ||
                !installed.Cells["Status"].Style.SelectionForeColor.IsEmpty)
            {
                throw new InvalidOperationException("Rechecking a package must clear its removal warning.");
            }
            installed.Cells["Selected"].Value = false;
            search.Text = "__hidden_selection__";
            VerifyButtonStates(main, true, "Apply selection");
            search.Clear();
            if (choices.Contains(optional.Id) || !packages.Rows.Cast<DataGridViewRow>()
                .Any(row => ((ShaderPackage)row.Tag!).Id == "missing-from-catalog"))
            {
                throw new InvalidOperationException("Filtering lost a pending removal or an installed catalog-missing package.");
            }
            installed = packages.Rows.Cast<DataGridViewRow>().Single(row => ((ShaderPackage)row.Tag!).Id == optional.Id);
            if (!Equals(installed.Cells["Status"].Value, "Installed (will be removed)") ||
                installed.Cells["Status"].Style.ForeColor != DarkTheme.Danger || !File.Exists(file))
            {
                throw new InvalidOperationException("Filtering must preserve pending-removal warnings without deleting files.");
            }
            TabControl tabs = Descendants(main).OfType<TabControl>().Single();
            int originalTab = tabs.SelectedIndex;
            tabs.SelectedIndex = 1;
            Capture(main, Path.Combine(gallery, "pending-shader-removal.png"));
            tabs.SelectedIndex = originalTab;
            installed = packages.Rows.Cast<DataGridViewRow>().Single(row => ((ShaderPackage)row.Tag!).Id == optional.Id);
            if (!choices.Contains(optional.Id) || !Equals(installed.Cells["Status"].Value, "Installed") ||
                !installed.Cells["Status"].Style.ForeColor.IsEmpty)
            {
                throw new InvalidOperationException("Tab changes must clear pending removals and restore saved checks.");
            }
            File.Delete(file);
            refresh.Invoke(main, null);
            installed = packages.Rows.Cast<DataGridViewRow>().Single(row => ((ShaderPackage)row.Tag!).Id == optional.Id);
            if (!Equals(installed.Cells["Status"].Value, "Incomplete") ||
                !installed.Cells["Status"].Style.ForeColor.IsEmpty)
            {
                throw new InvalidOperationException("Missing managed shader files must show an incomplete installation.");
            }
        }
        finally
        {
            stateProperty.SetValue(manager, null);
            choices.Clear();
            choices.UnionWith(originalChoices);
            search.Clear();
            refresh.Invoke(main, null);
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
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(path, ImageFormat.Png);
        VerifyCommandLayout(form);
    }

    private static void VerifyCommandLayout(Form form)
    {
        foreach (Button purge in Descendants(form).OfType<Button>().Where(control =>
            control.Visible && control.Text == "Purge shader folder..."))
        {
            foreach (Button command in purge.Parent!.Controls.OfType<Button>())
            {
                if (!purge.Parent.ClientRectangle.Contains(command.Bounds))
                {
                    throw new InvalidOperationException("Shader package commands must fit within their toolbar.");
                }
            }
        }

        foreach (Button browse in Descendants(form).OfType<Button>().Where(control =>
            control.AccessibleName is "Browse for a file" or "Browse for a folder"))
        {
            TextBox input = browse.Parent!.Controls.OfType<TextBox>().Single();
            if (input.Height != browse.Height || input.Top != browse.Top || input.Bottom > input.Parent!.ClientSize.Height)
            {
                throw new InvalidOperationException($"The browse button is misaligned with its input in '{form.Text}'.");
            }
        }

        if (form is GameDialog)
        {
            TableLayoutPanel layout = Descendants(form).OfType<TableLayoutPanel>()
                .Single(table => table.Controls.OfType<ComboBox>().Any());
            VerifyInstallDialogSpacing(form, layout);
            foreach (Label label in layout.Controls.OfType<Label>())
            {
                int row = layout.GetRow(label);
                Control input = layout.GetControlFromPosition(1, row)!;
                int labelCenter = label.Top + (label.Height / 2);
                int inputCenter = input.Top + (input.Height / 2);
                if (Math.Abs(labelCenter - inputCenter) > 1)
                {
                    throw new InvalidOperationException($"The '{label.Text}' label is misaligned with its input.");
                }
            }
            foreach (Control control in layout.Controls.Cast<Control>().Where(control => control.Visible))
            {
                if (control.Left < layout.Padding.Left || control.Top < layout.Padding.Top ||
                    control.Right > layout.ClientSize.Width - layout.Padding.Right ||
                    control.Bottom > layout.ClientSize.Height - layout.Padding.Bottom)
                {
                    throw new InvalidOperationException($"The install dialog clips {control.GetType().Name} " +
                        $"'{control.AccessibleName ?? control.Text}' at {control.Bounds} within {layout.ClientSize}.");
                }
            }
        }

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

    private static void VerifyInstallDialogSpacing(Form form, TableLayoutPanel layout)
    {
        TableLayoutPanel frame = form.Controls.OfType<TableLayoutPanel>().Single();
        var viewport = (Panel)layout.Parent!;
        Control browse = Descendants(form).OfType<TextBox>()
            .Single(control => control.AccessibleName == "Game executable").Parent!;
        LinkLabel hint = Descendants(form).OfType<LinkLabel>().Single();
        ApiCheckIcon icon = Descendants(form).OfType<ApiCheckIcon>().Single();
        Control detection = hint.Parent!;
        Label warning = Descendants(form).OfType<Label>()
            .Single(control => control.AccessibleName == "Installation changes");
        CheckBox confirmation = Descendants(form).OfType<CheckBox>().Single();
        Control notice = warning.Parent!.Parent!;
        ComboBox api = Descendants(form).OfType<ComboBox>().Single(combo => combo.Items.Contains(RenderApi.Vulkan));
        ComboBox build = Descendants(form).OfType<ComboBox>().Single(combo =>
            combo.Items.Contains(RuntimeBuild.Standard));
        var accept = (Button)form.AcceptButton!;
        Control footer = accept.Parent!;
        int smallGap = browse.Margin.Top;
        int fieldGap = api.Margin.Top;
        int footerGap = footer.Margin.Top;
        Control beforeApi = warning.Visible ? notice : browse;
        bool detectionSpacingValid = detection.Visible
            ? detection.Top == layout.Padding.Top && browse.Top - detection.Bottom == smallGap
            : browse.Top == layout.Padding.Top + smallGap;
        if (!detectionSpacingValid ||
            (warning.Visible && notice.Top - browse.Bottom != notice.Margin.Top) ||
            api.Top - beforeApi.Bottom != fieldGap || build.Top - api.Bottom != build.Margin.Top ||
            footer.Top - viewport.Bottom != footerGap)
        {
            throw new InvalidOperationException("Install dialog gaps changed: " +
                $"detection {detection.Top}/{layout.Padding.Top}, " +
                $"browse {browse.Top - detection.Bottom}/{smallGap}, " +
                $"notice {notice.Top - browse.Bottom}/{notice.Margin.Top}, " +
                $"API {api.Top - beforeApi.Bottom}/{fieldGap}, " +
                $"build {build.Top - api.Bottom}/{build.Margin.Top}, " +
                $"footer {footer.Top - viewport.Bottom}/{footerGap}. " +
                $"API bounds {api.Bounds}, preferred {api.PreferredSize}, " +
                $"row {layout.GetRowHeights()[layout.GetRow(api)]}. " +
                $"Build bounds {build.Bounds}, preferred {build.PreferredSize}.");
        }

        if (browse.Left != api.Left || build.Left != api.Left || detection.Left != api.Left ||
            hint.Left != icon.Right + icon.Margin.Right || hint.Right > detection.ClientSize.Width ||
            (warning.Visible && notice.Left != api.Left) ||
            frame.Right != form.ClientSize.Width || frame.Bottom != form.ClientSize.Height ||
            viewport.Left != frame.Padding.Left || viewport.Right != form.ClientSize.Width - frame.Padding.Right ||
            footer.Right != form.ClientSize.Width - frame.Padding.Right || footer.Left != frame.Padding.Left ||
            footer.Bottom != form.ClientSize.Height - frame.Padding.Bottom ||
            accept.Top != 0 || accept.Right != footer.ClientSize.Width || accept.Bottom != footer.ClientSize.Height)
        {
            throw new InvalidOperationException("Install dialog fields and buttons must share the same outer padding.");
        }

        if (layout.Bottom > viewport.ClientSize.Height ||
            viewport.VerticalScroll.Visible || viewport.HorizontalScroll.Visible)
        {
            throw new InvalidOperationException("Wizard fields must fit without scrolling the whole form.");
        }

        if (confirmation.Visible)
        {
            int confirmationCenter = confirmation.Top + (confirmation.Height / 2);
            int buttonCenter = accept.Top + (accept.Height / 2);
            int firstButton = footer.Controls.OfType<Button>().Min(button => button.Left);
            if (confirmation.Parent != footer || !footer.ClientRectangle.Contains(confirmation.Bounds) ||
                confirmation.Right + confirmation.Margin.Right > firstButton ||
                Math.Abs(confirmationCenter - buttonCenter) > 1 ||
                confirmation.Width < confirmation.GetPreferredSize(Size.Empty).Width ||
                confirmation.CheckAlign != ContentAlignment.MiddleRight || !confirmation.Font.Bold ||
                confirmation.Font.Size != form.Font.Size || confirmation.Font.FontFamily.Name != form.Font.FontFamily.Name)
            {
                throw new InvalidOperationException("Overwrite confirmation must fit beside the buttons, " +
                    "keep its checkbox on the right and use the dialog font size with bold text.");
            }
        }

        if (warning.Visible && (!warning.AutoSize || warning.UseMnemonic ||
            warning.Height < warning.GetPreferredSize(new Size(warning.Width, 0)).Height))
        {
            throw new InvalidOperationException("Inline installation information must wrap without clipping its text.");
        }

        Rectangle screen = Screen.FromControl(form).WorkingArea;
        if (form.Visible && !screen.Contains(form.Bounds))
        {
            throw new InvalidOperationException("The install dialog must fit within the screen working area.");
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
