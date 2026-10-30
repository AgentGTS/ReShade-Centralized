using System.ComponentModel;
using System.Diagnostics;

namespace ReShadeCentralized;

internal sealed partial class GameDialog : Form
{
    private readonly IReadOnlyList<OwnedLink> managedLinks;
    private readonly ManagerState state;
    public InstallationPlan? ReviewedPlan { get; private set; }
    public bool ReplacementConfirmed => replaceConfirmation.Checked;
    private readonly RuntimeBuild global;
    private readonly GameProfile original;
    private CancellationTokenSource? detectionCancellation;
    private int executableVersion;
    private RuntimeBuild? nonVulkanBuild;
    private bool usingGlobalBuild;
    private bool dialogClosing;
    private bool sizingDialog;
    private Font? confirmationFont;

    public GameProfile Result()
    {
        if (api.SelectedItem is not RenderApi selectedApi || build.SelectedItem is not RuntimeBuild selectedBuild)
        {
            throw new InvalidOperationException("Select a graphics API and ReShade build before installing.");
        }

        GameProfile game = original.Clone();
        game.Executable = GameTargetResolver.Resolve(executable.Text.Trim()).Executable;
        game.Name = Path.GetFileName(game.Executable);
        game.Api = selectedApi;
        game.Build = game.Api == RenderApi.Vulkan ? global : selectedBuild;
        return game;
    }

    private void UpdateBuild()
    {
        bool vulkan = api.SelectedItem is RenderApi.Vulkan;
        if (vulkan && !usingGlobalBuild)
        {
            nonVulkanBuild = build.SelectedItem as RuntimeBuild?;
            build.SelectedItem = global;
        }
        else if (!vulkan && usingGlobalBuild)
        {
            build.SelectedItem = nonVulkanBuild.HasValue ? nonVulkanBuild.Value : BuildPlaceholder;
        }

        usingGlobalBuild = vulkan;
        build.Enabled = !vulkan;
        buildLabel.Text = vulkan ? "Global build" : "Build";
        UpdateInstallationNotice();
    }

    private async Task DetectApiAsync()
    {
        if (dialogClosing || IsDisposed || Disposing)
        {
            return;
        }

        CancelApiDetection();
        string? path = PortableExecutable.GetValidatedGamePath(executable.Text);
        if (path == null)
        {
            SetDetectionHint("Choose a valid local x86/x64 .exe outside Windows, then check again.", ApiCheckState.Warning);
            return;
        }

        using var cancellation = new CancellationTokenSource();
        detectionCancellation = cancellation;
        int currentExecutable = executableVersion;
        SetDetectionHint("Checking graphics API...", ApiCheckState.Checking);
        try
        {
            RenderApi? detected = await Task.Run(() => GraphicsApiDetector.Detect(path, cancellation.Token), cancellation.Token);
            if (!CanDisplayDetection(currentExecutable, cancellation.Token))
            {
                return;
            }

            if (detected.HasValue)
            {
                SetDetectionHint($"Recommended API: {DisplayNames.For(detected.Value)}.", ApiCheckState.Detected);
            }
            else
            {
                SetDetectionHint("Could not identify the graphics API. Select it below.", ApiCheckState.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            // A changed path or closed dialog makes this result obsolete.
        }
        catch (Exception error)
        {
            // Recommendations are optional and must not interrupt a confirmed installation.
            Trace.TraceWarning($"Graphics API recommendation failed: {error}");
            if (CanDisplayDetection(currentExecutable, cancellation.Token))
            {
                SetDetectionHint("Could not check the graphics API. Select it manually.", ApiCheckState.Warning);
            }
        }
        finally
        {
            if (ReferenceEquals(detectionCancellation, cancellation))
            {
                detectionCancellation = null;
            }
        }
    }

    private bool CanDisplayDetection(int version, CancellationToken cancellationToken) =>
        !dialogClosing && !IsDisposed && !Disposing && !cancellationToken.IsCancellationRequested &&
        version == executableVersion;

    private void CancelApiDetection()
    {
        executableVersion++;
        detectionCancellation?.Cancel();
    }

    private void HighlightCheckLink(bool highlighted)
    {
        if (detectionIcon.State != ApiCheckState.Ready)
        {
            return;
        }

        Color color = highlighted ? DarkTheme.Accent : DarkTheme.Muted;
        detectionHint.LinkColor = color;
        detectionIcon.ForeColor = color;
    }

    private void SetDetectionHint(string text, ApiCheckState state)
    {
        detectionHint.Links.Clear();
        detectionHint.Text = text;
        if (state == ApiCheckState.Ready)
        {
            detectionHint.Links.Add(0, text.Length);
        }
        Color color = state switch
        {
            ApiCheckState.Detected => DarkTheme.Accent,
            ApiCheckState.Warning => DarkTheme.Warning,
            _ => DarkTheme.Muted
        };
        detectionHint.LinkColor = DarkTheme.Muted;
        detectionHint.ForeColor = color;
        detectionIcon.ForeColor = color;
        detectionIcon.State = state;
        detectionPanel.Visible = text.Length > 0;
        UpdateDialogSize();
    }

    private void UpdateInstallationNotice()
    {
        ExistingInstallationNotice? notice;
        InstallationPlan? plan = null;
        try
        {
            if (PortableExecutable.GetValidatedGamePath(executable.Text.Trim()) != null &&
                api.SelectedItem is RenderApi && build.SelectedItem is RuntimeBuild)
            {
                GameProfile game = Result();
                game.Directory = ProfilePaths.DirectoryFor(state, game);
                plan = InstallationPlanner.CreatePlan(state, game);
                string message = plan.Describe(includePaths: false);
                if (!LocalFiles.Same(game.Executable, executable.Text.Trim()))
                {
                    message = "Resolved executable: " + game.Executable + "\n\n" + message;
                }
                notice = new(message, plan.Conflict != null);
            }
            else
            {
                notice = InstallationInspection.InspectExistingInstallation(
                    executable.Text.Trim(), api.SelectedItem as RenderApi?, managedLinks, includePaths: false);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            ArgumentException or Win32Exception)
        {
            notice = new("Cannot review this installation. " + error.Message, true);
        }

        if (ReviewedPlan?.Fingerprint != plan?.Fingerprint)
        {
            // An acknowledgement applies only to the exact files and destinations displayed.
            replaceConfirmation.Checked = false;
        }

        ReviewedPlan = plan;
        replaceConfirmation.Visible = plan?.RequiresConfirmation == true && plan.Conflict == null;
        string path = executable.Text.Trim();
        installButton.Enabled = PortableExecutable.GetValidatedGamePath(path) != null &&
            api.SelectedItem is RenderApi && build.SelectedItem is RuntimeBuild &&
            notice?.BlocksInstallation != true && plan != null &&
            (!plan.RequiresConfirmation || replaceConfirmation.Checked);
        warningText.Text = (notice?.Message ?? "").ReplaceLineEndings(Environment.NewLine);
        warningText.ForeColor = notice?.BlocksInstallation == true || plan?.RequiresConfirmation == true ?
            DarkTheme.Warning : DarkTheme.Muted;
        warningPanel.Visible = notice != null;
        warningPanel.Invalidate(true);
        UpdateDialogSize();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (!e.Cancel)
        {
            dialogClosing = true;
            CancelApiDetection();
            detectionIcon.Visible = false;
        }
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Enter && executable.ContainsFocus)
        {
            return true;
        }

        return base.ProcessDialogKey(keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            dialogClosing = true;
            CancelApiDetection();
            confirmationFont?.Dispose();
        }
        base.Dispose(disposing);
    }
}
