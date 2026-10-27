using System.Diagnostics;
using System.Reflection;

namespace ReShadeCentralized;

internal static partial class UiPreview
{
    private static void SendSystemClose(MainForm form)
    {
        Message message = Message.Create(form.Handle, 0x0112, new IntPtr(0xF060), IntPtr.Zero);
        typeof(Form).GetMethod("WndProc", LoadingFlags)!.Invoke(form, [message]);
    }

    internal static void RunShutdownProbe(string[] args)
    {
        string mode = Argument(args, "--shutdown-probe") ?? "";
        string root = Path.GetFullPath(Argument(args, "--probe-root") ?? "");
        string marker = Path.Combine(root, "allow-shutdown-probe.txt");
        if (!File.Exists(marker) || Directory.EnumerateFileSystemEntries(root).Any(path => path != marker) ||
            mode is not ("timeout" or "repeat" or "ui-stall" or "fatal-ui" or "fatal-worker" or
                "fatal-startup" or "fatal-log-failure"))
        {
            throw new InvalidOperationException("Shutdown probes require a fresh, explicitly marked test directory.");
        }
        FatalErrors.LogPath = mode == "fatal-log-failure" ? root : Path.Combine(root, "Crash.log");
        string owned = Path.Combine(root, "owned.txt");
        File.WriteAllText(owned, "original");
        var transaction = new FileTransaction(Path.Combine(root, "Recovery-v2"), "shutdown probe");
        transaction.WriteText(owned, "pending mutation");
        if (mode == "fatal-startup")
        {
            File.WriteAllText(Path.Combine(root, "started.txt"), mode);
            throw new InvalidOperationException("Synthetic fatal startup failure");
        }
        var manager = new Manager(StateStore.ForApplication(Path.Combine(root, "app")), new LoadingRegistry());
        using var form = new MainForm(manager, initializeOnShow: false) { Opacity = 0, ShowInTaskbar = false };
        form.Shown += (_, _) => form.BeginInvoke(() =>
        {
            File.WriteAllText(Path.Combine(root, "started.txt"), mode);
            if (mode is "fatal-ui" or "fatal-log-failure")
            {
                throw new InvalidOperationException("Synthetic fatal UI failure: " + mode);
            }
            if (mode == "fatal-worker")
            {
                new Thread(() => throw new InvalidOperationException("Synthetic fatal worker failure")).Start();
                return;
            }
            _ = RunLoadingCheck(form, _ => Task.Delay(Timeout.InfiniteTimeSpan));
            if (!form.Enabled || !form.ControlBox || !form.MinimizeBox || !form.MaximizeBox)
            {
                throw new InvalidOperationException("Loading must not disable the window or its title-bar controls.");
            }
            SendSystemClose(form);
            if (!form.Enabled || !form.ControlBox)
            {
                throw new InvalidOperationException("A pending close must keep window controls available.");
            }
            if (mode == "repeat")
            {
                SendSystemClose(form);
            }
            if (mode == "ui-stall")
            {
                // Deliberately block only this disposable child process after the close watchdog is armed.
                Thread.Sleep(Timeout.Infinite);
            }
        });
        Application.Run(form);
        throw new InvalidOperationException("A shutdown probe unexpectedly returned without terminating.");
    }

    private static async Task VerifyExitSafetyAsync(string gallery)
    {
        var forced = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var deadline = new ShutdownDeadline(code => forced.TrySetResult(code), TimeSpan.FromMilliseconds(60)))
        {
            deadline.Request();
            if (await forced.Task.WaitAsync(TimeSpan.FromSeconds(2)) != 2)
            {
                throw new InvalidOperationException("A stuck shutdown must reach its force-exit deadline.");
            }
        }
        var forcedAfterDispose = 0;
        using (var deadline = new ShutdownDeadline(_ => Interlocked.Increment(ref forcedAfterDispose),
            TimeSpan.FromMilliseconds(40)))
        {
            deadline.Request();
        }
        await Task.Delay(90);
        if (forcedAfterDispose != 0)
        {
            throw new InvalidOperationException("A completed shutdown must disarm its deadline.");
        }

        string output = Path.Combine(gallery, "exit-probes");
        Directory.CreateDirectory(output);
        foreach (string mode in new[]
            { "timeout", "repeat", "ui-stall", "fatal-ui", "fatal-worker", "fatal-startup", "fatal-log-failure" })
        {
            string root = Path.GetFullPath(Path.Combine(output, mode));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "allow-shutdown-probe.txt"), "isolated exit validation");
            string entry = Environment.GetCommandLineArgs()[0];
            bool managedEntry = entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            var start = new ProcessStartInfo(managedEntry ? "dotnet" : Environment.ProcessPath!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (managedEntry)
            {
                start.ArgumentList.Add(entry);
            }
            start.ArgumentList.Add("--shutdown-probe");
            start.ArgumentList.Add(mode);
            start.ArgumentList.Add("--probe-root");
            start.ArgumentList.Add(root);
            using Process process = Process.Start(start) ??
                throw new IOException("Cannot start the exit validation child.");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            var watch = Stopwatch.StartNew();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));
            }
            catch (TimeoutException)
            {
                process.Kill();
                await process.WaitForExitAsync();
                throw new InvalidOperationException("The isolated shutdown child did not exit: " + mode);
            }
            string error = await stderr;
            File.WriteAllText(Path.Combine(root, "child-output.txt"), await stdout + error);
            int expected = mode.StartsWith("fatal-") ? 1 : 2;
            if (!File.Exists(Path.Combine(root, "started.txt")) || process.ExitCode != expected)
            {
                throw new InvalidOperationException(
                    $"Exit probe {mode}: expected {expected}, got {process.ExitCode}: {error}");
            }
            if ((mode is "fatal-ui" or "fatal-worker" or "fatal-startup") &&
                !File.ReadAllText(Path.Combine(root, "Crash.log")).Contains("Synthetic fatal"))
            {
                throw new InvalidOperationException("A fatal failure must attempt to retain its diagnostic log.");
            }
            if (File.ReadAllText(Path.Combine(root, "owned.txt")) != "pending mutation")
            {
                throw new InvalidOperationException("The probe must leave a real interrupted transaction to recover.");
            }
            FileTransaction.RecoverPending(Path.Combine(root, "Recovery-v2"), new LoadingRegistry());
            if (File.ReadAllText(Path.Combine(root, "owned.txt")) != "original")
            {
                throw new InvalidOperationException(
                    "The next launch must be able to recover interrupted managed files.");
            }
            Console.WriteLine($"PASS exit probe {mode}: code={process.ExitCode}, " +
                $"elapsed_ms={watch.ElapsedMilliseconds}, recovered");
        }
    }
}
