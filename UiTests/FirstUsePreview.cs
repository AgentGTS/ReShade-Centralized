using System.Net;

namespace ReShadeCentralized;

internal static partial class UiPreview
{
    private static void VerifyLoadingControlsLocked(MainForm form)
    {
        if (Descendants(form).Any(control => control.Enabled))
        {
            throw new InvalidOperationException("All main-window controls must remain locked during loading.");
        }
        bool openedHelp = false;
        using var dismissHelp = new System.Windows.Forms.Timer { Interval = 20 };
        dismissHelp.Tick += (_, _) =>
        {
            foreach (HelpDialog dialog in Application.OpenForms.OfType<HelpDialog>().ToArray())
            {
                openedHelp = true;
                dialog.Close();
            }
        };
        dismissHelp.Start();
        Descendants(form).OfType<Button>().Single(button => button.AccessibleName == "Help").PerformClick();
        var request = new HelpEventArgs(Point.Empty);
        typeof(Control).GetMethod("OnHelpRequested", LoadingFlags)!.Invoke(form, [request]);
        dismissHelp.Stop();
        if (openedHelp || !request.Handled)
        {
            throw new InvalidOperationException("Neither the Help button nor F1 may open a dialog while loading.");
        }
    }

    private sealed class FirstUseCatalogHandler : HttpMessageHandler
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Requests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("Compatibility.ini") ?
                    "[fixture.exe]\nRenderApi=DXGI\n" :
                    "[standard]\nRequired=1\nRepositoryUrl=https://github.com/test/standard\n" +
                    "DownloadUrl=https://example.test/standard.zip\n")
            };
        }
    }

    private static async Task VerifyFirstUseAsync(string gallery)
    {
        foreach (string scenario in new[] { "new", "empty", "existing", "cancel", "invalid", "file" })
        {
            string root = Path.Combine(gallery, "first-use-" + scenario);
            string application = Path.Combine(root, "app");
            string central = Path.Combine(root, "central storage");
            Directory.CreateDirectory(application);
            if (scenario == "empty")
            {
                Directory.CreateDirectory(central);
                File.WriteAllText(Path.Combine(central, "keep.txt"), "Unrelated file");
            }
            string occupiedFile = Path.Combine(root, "occupied.txt");
            if (scenario == "file")
            {
                File.WriteAllText(occupiedFile, "Preserve this file");
            }
            if (scenario == "existing")
            {
                CreateLoadingFixture(central, 1);
            }
            string? originalState = scenario == "existing" ?
                File.ReadAllText(Path.Combine(central, "ReShadeCentralized.json")) : null;
            using var handler = new FirstUseCatalogHandler();
            using var manager = new Manager(StateStore.ForApplication(application), new LoadingRegistry(),
                new Downloads(handler));
            var errors = new List<Exception>();
            Exception? dialogError = null;
            var setups = 0;
            MainForm? form = null;
            SetupDialog CreateSetup()
            {
                setups++;
                var setup = new SetupDialog { Opacity = 0, ShowInTaskbar = false };
                setup.Shown += (_, _) => setup.BeginInvoke(() =>
                {
                    try
                    {
                        if (Descendants(form!).Any(control => control.Enabled) || !setup.Enabled ||
                            !((Button)setup.AcceptButton!).Enabled)
                        {
                            throw new InvalidOperationException(
                                "Only the required Setup dialog may accept first-use input.");
                        }
                        TextBox folder = Descendants(setup).OfType<TextBox>().Single();
                        if (!folder.Enabled || folder.Text != StateStore.DefaultDirectory)
                        {
                            throw new InvalidOperationException(
                                "First use must offer an editable default storage path.");
                        }
                        if (scenario == "cancel")
                        {
                            ((Button)setup.CancelButton!).PerformClick();
                            return;
                        }
                        folder.Text = scenario == "invalid" ? application : scenario == "file" ? occupiedFile : central;
                        ((Button)setup.AcceptButton!).PerformClick();
                    }
                    catch (Exception error)
                    {
                        dialogError = error;
                        setup.DialogResult = DialogResult.Cancel;
                        setup.Close();
                    }
                });
                return setup;
            }
            using (form = new MainForm(manager, initializeOnShow: false, errorReporter: errors.Add,
                setupDialogFactory: CreateSetup) { Opacity = 0, ShowInTaskbar = false })
            {
                form.Show();
                Task startup = InitializeForLoadingCheck(form);
                if (scenario is "new" or "empty")
                {
                    await WaitForLoadingCheckAsync(() => handler.Started.Task.IsCompleted || startup.IsCompleted);
                    if (!handler.Started.Task.IsCompleted || manager.State == null ||
                        Descendants(form).Any(control => control.Enabled))
                    {
                        throw new InvalidOperationException(
                            "A created store must remain locked until catalogs and binding finish.");
                    }
                    VerifyLoadingControlsLocked(form);
                    Capture(form, Path.Combine(gallery, "loading-first-use-" + scenario + ".png"));
                    handler.Release.TrySetResult();
                }
                await startup;
                if (dialogError != null)
                {
                    throw new InvalidOperationException("First-use dialog validation failed.", dialogError);
                }
                if (setups != 1)
                {
                    throw new InvalidOperationException("An unconfigured launch must show Setup exactly once.");
                }
                if (scenario is "cancel" or "invalid" or "file")
                {
                    if (File.Exists(manager.Store.PointerPath) || Directory.Exists(central) || handler.Requests != 0 ||
                        manager.State != null || (scenario == "cancel" && !form.IsDisposed) ||
                        (scenario != "cancel" && errors.Count != 1) ||
                        (scenario == "file" && File.ReadAllText(occupiedFile) != "Preserve this file"))
                    {
                        throw new InvalidOperationException(
                            "Canceled/invalid setup must not create storage or save a pointer.");
                    }
                    continue;
                }
                StorageLoadingIndicator indicator = Descendants(form).OfType<StorageLoadingIndicator>().Single();
                Button help = Descendants(form).OfType<Button>().Single(button => button.AccessibleName == "Help");
                if (errors.Count != 0 || indicator.Visible || !help.Enabled || manager.State == null ||
                    manager.State.Paths.Root != central || File.ReadAllText(manager.Store.PointerPath!) != central ||
                    Descendants(form).OfType<GamesGrid>().Single().Rows.Count != (scenario == "existing" ? 1 : 0))
                {
                    throw new InvalidOperationException(
                        "Valid first-use setup must create/open the chosen store and unlock after binding.");
                }
                if (scenario == "existing" && (handler.Requests != 0 ||
                    File.ReadAllText(manager.Store.StatePath) != originalState))
                {
                    throw new InvalidOperationException(
                        "Selecting existing storage must preserve state and use its cached catalogs.");
                }
                if (scenario == "empty" && File.ReadAllText(Path.Combine(central, "keep.txt")) != "Unrelated file")
                {
                    throw new InvalidOperationException(
                        "First-use setup must preserve unrelated files in the chosen folder.");
                }
            }
        }
        Console.WriteLine("PASS First use: new/empty/existing storage, cancel, invalid app-folder/file paths, " +
            "modal Setup input, and full main-window lock through catalog preparation.");
    }
}
