using System.Diagnostics;

namespace ReShadeCentralized;

public sealed partial class MainForm
{
    private readonly RuntimeUpdateService runtimeUpdateService = new();
    private readonly ToolTip runtimeUpdateToolTip = new();
    private readonly RefreshButton runtimeUpdateButton = new()
    {
        AccessibleName = "Check for ReShade updates",
        AccessibleDescription = "Check the official GitHub repository for a newer stable ReShade version."
    };

    private readonly FlowLayoutPanel runtimeUpdateNotice = new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Anchor = AnchorStyles.Left,
        WrapContents = false,
        Margin = Padding.Empty,
        Visible = false
    };

    private readonly ApiCheckIcon runtimeUpdateIcon = new()
    {
        State = ApiCheckState.Detected,
        ForeColor = DarkTheme.Accent,
        Margin = new Padding(0, 3, 6, 0),
        AccessibleName = "ReShade update information",
        AccessibleDescription = "An updated ReShade runtime is available."
    };

    private readonly LinkLabel runtimeUpdateLabel = new()
    {
        AutoSize = true,
        LinkColor = DarkTheme.Accent,
        ActiveLinkColor = DarkTheme.Accent,
        VisitedLinkColor = DarkTheme.Accent,
        DisabledLinkColor = DarkTheme.Muted,
        LinkBehavior = LinkBehavior.HoverUnderline,
        ForeColor = DarkTheme.Muted,
        Margin = Padding.Empty,
        AccessibleName = "ReShade update status"
    };

    private CancellationTokenSource? runtimeUpdateCancellation;
    private Version? latestRuntimeVersion;
    private RuntimeUpdateState runtimeUpdateState;
    private bool runtimeStartupCheckStarted;

    private enum RuntimeUpdateState
    {
        NotChecked,
        Checking,
        Verified,
        Unavailable
    }

    private void BuildRuntimeUpdateNotice(TableLayoutPanel footer)
    {
        runtimeUpdateNotice.Controls.Add(runtimeUpdateIcon);
        runtimeUpdateNotice.Controls.Add(runtimeUpdateLabel);
        runtimeUpdateNotice.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "|",
            Margin = new Padding(3, 0, 0, 0)
        });
        footer.Controls.Add(runtimeUpdateNotice, 1, 0);
        runtimeUpdateLabel.LinkClicked += (_, _) =>
        {
            if (runtimeUpdateLabel.Links.Count > 0 && operationCancellation == null
                && !storageControlsLocked && !closeAfterOperation)
            {
                tabs.SelectedIndex = 2;
            }
        };
        runtimeUpdateButton.Click += async (_, _) => await CheckRuntimeUpdatesAsync();
        runtimeUpdateToolTip.SetToolTip(runtimeUpdateButton, "Check for ReShade updates");
        runtimeUpdateLabel.MouseEnter += (_, _) =>
        {
            if (runtimeUpdateLabel.Links.Count > 0)
            {
                runtimeUpdateLabel.BackColor = DarkTheme.Hover;
            }
        };
        runtimeUpdateLabel.MouseLeave += (_, _) => runtimeUpdateLabel.BackColor = Color.Transparent;
        FormClosing += (_, _) => runtimeUpdateCancellation?.Cancel();
    }

    private void CheckRuntimeUpdatesOnStartup()
    {
        if (runtimeStartupCheckStarted || storage.State == null || presentation == null
            || !ReferenceEquals(presentation.State, storage.State)
            || storageLoadFailed || storageControlsLocked || operationCancellation != null
            || runtimeUpdateCancellation != null || closeAfterOperation || IsDisposed || Disposing)
        {
            return;
        }

        runtimeStartupCheckStarted = true;
        _ = CheckRuntimeUpdatesAsync();
    }

    private async Task CheckRuntimeUpdatesAsync()
    {
        if (runtimeUpdateCancellation != null || closeAfterOperation || IsDisposed || Disposing)
        {
            return;
        }

        if (storage.State == null || storageLoadFailed)
        {
            runtimeUpdateState = RuntimeUpdateState.Unavailable;
            latestRuntimeVersion = null;
            RefreshRuntimeUpdateNotice();
            return;
        }

        using var cancellation = new CancellationTokenSource();
        runtimeUpdateCancellation = cancellation;
        runtimeUpdateState = RuntimeUpdateState.Checking;
        latestRuntimeVersion = null;
        runtimeUpdateButton.Enabled = false;
        RefreshRuntimeUpdateNotice();

        try
        {
            Version available = await runtimeUpdateService.CheckAsync(cancellation.Token);
            if (cancellation.IsCancellationRequested || IsDisposed || Disposing || closeAfterOperation)
            {
                return;
            }

            latestRuntimeVersion = available;
            runtimeUpdateState = RuntimeUpdateState.Verified;
            RefreshRuntimeUpdateNotice();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            Trace.TraceWarning("ReShade update check unavailable: {0}", error.Message);
            if (!cancellation.IsCancellationRequested && !IsDisposed && !Disposing && !closeAfterOperation)
            {
                runtimeUpdateState = RuntimeUpdateState.Unavailable;
                RefreshRuntimeUpdateNotice();
            }
        }
        finally
        {
            if (ReferenceEquals(runtimeUpdateCancellation, cancellation))
            {
                runtimeUpdateCancellation = null;
            }

            if (!IsDisposed && !Disposing && !closeAfterOperation)
            {
                runtimeUpdateButton.Enabled = true;
            }
        }
    }

    private void RefreshRuntimeUpdateNotice()
    {
        runtimeUpdateLabel.Links.Clear();
        runtimeUpdateLabel.BackColor = Color.Transparent;
        runtimeUpdateLabel.Text = "";
        runtimeUpdateIcon.Visible = false;
        bool showStatus = runtimeUpdateState is RuntimeUpdateState.Verified or RuntimeUpdateState.Unavailable;
        runtimeUpdateNotice.Visible = showStatus;
        if (!showStatus)
        {
            return;
        }

        string description = "The ReShade update status could not be verified. Use the check button to try again.";
        bool updateAvailable = false;
        runtimeUpdateLabel.Text = "ReShade update unavailable";
        if (runtimeUpdateState == RuntimeUpdateState.Verified && latestRuntimeVersion != null && presentation != null)
        {
            var installedVersions = new List<Version>();
            foreach (string text in presentation.RuntimeVersions.Values)
            {
                if (Version.TryParse(text, out Version? installed))
                {
                    installedVersions.Add(new Version(installed.Major, installed.Minor, Math.Max(0, installed.Build)));
                }
            }

            Version? newestInstalled = installedVersions.Max();
            if (newestInstalled != null && newestInstalled < latestRuntimeVersion)
            {
                runtimeUpdateLabel.Text = $"ReShade {latestRuntimeVersion} update available";
                updateAvailable = true;
                runtimeUpdateIcon.Visible = true;
                description = $"ReShade {latestRuntimeVersion} is available. " +
                    "Open Runtime & storage > Update ReShade. Close affected games before updating.";
            }
            else if (newestInstalled != null)
            {
                runtimeUpdateLabel.Text = "ReShade is up to date";
                description = $"At least one installed ReShade build is up to date with official stable version {latestRuntimeVersion}. " +
                    "Standard and add-on builds are compared using the newest known installed version.";
            }
        }

        runtimeUpdateLabel.LinkArea = updateAvailable
            ? new LinkArea(0, runtimeUpdateLabel.Text.Length) : new LinkArea(0, 0);
        runtimeUpdateLabel.AccessibleDescription = description;
        runtimeUpdateIcon.AccessibleDescription = description;
        runtimeUpdateToolTip.SetToolTip(runtimeUpdateLabel, description);
        runtimeUpdateToolTip.SetToolTip(runtimeUpdateIcon, description);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            runtimeUpdateCancellation?.Cancel();
            runtimeUpdateService.Dispose();
            runtimeUpdateToolTip.Dispose();
        }

        base.Dispose(disposing);
    }
}
