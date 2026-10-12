using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Sg.Core;

namespace Sg.App;

/// <summary>
/// Update and restart in one dialog: it says what happens, and Update turns it into the download's
/// progress. The app asks GitHub again, downloads, unpacks and checks the build while it still runs, so
/// a slow or failed download is on screen with its reason and Cancel leaves everything as it was. Only a
/// build that is ready to install comes back; the caller hands it to sg.exe and closes.
/// </summary>
public static class UpdateDialog
{
    public static async Task<FetchedUpdate?> Run(object owner, UpdateCheck known)
    {
        var size = known.Size > 0 ? " (" + DiskUsage.Human(known.Size) + ")" : "";
        var intro = new TextBlock
        {
            Text = $"sg downloads {known.Remote}{size}, then closes, installs it over {Updates.InstallDir()} and opens again.\n\n"
                   + "Anything you typed in another sg window is lost.",
            TextWrapping = TextWrapping.Wrap,
        };
        var bar = new ProgressBar { Maximum = 100, IsIndeterminate = true, Visibility = Visibility.Collapsed };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(bar, "UpdateProgress");
        AutomationProperties.SetAutomationId(status, "UpdateStatus");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        var panel = new StackPanel { Spacing = 12, MinWidth = 420 };
        panel.Children.Add(intro);
        panel.Children.Add(bar);
        panel.Children.Add(status);

        var d = new ContentDialog
        {
            XamlRoot = Dialogs.RootOf(owner),
            Title = "Update and restart",
            Content = panel,
            PrimaryButtonText = "Update",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        CancellationTokenSource? running = null;
        FetchedUpdate? ready = null;
        var finished = false;
        var closed = false;

        void Say(string text, bool busy)
        {
            status.Text = text;
            status.Visibility = Visibility.Visible;
            bar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        }

        void Show(string label, long done, long total)
        {
            if (closed) return;
            if (label == "download")
            {
                bar.IsIndeterminate = total <= 0;
                if (total > 0) bar.Value = 100.0 * done / total;
                Say(total > 0
                    ? $"Downloading {DiskUsage.Human(done)} of {DiskUsage.Human(total)}, {100 * done / total}%"
                    : $"Downloading {DiskUsage.Human(done)}", busy: true);
            }
            else if (label == "unpack")
            {
                bar.IsIndeterminate = true;
                Say("Checking the download…", busy: true);
            }
        }

        // A task that started while the build downloaded would die with the app, so the restart waits for it.
        void Finish()
        {
            if (Session.Tasks.Snapshot().FirstOrDefault(t => t.Active) is { } task)
            {
                Say($"Downloaded. Wait for {task.Title} to finish, then press Restart. Cancel deletes the download.", busy: false);
                d.PrimaryButtonText = "Restart";
                d.IsPrimaryButtonEnabled = true;
                return;
            }
            Say("Restarting…", busy: true);
            finished = true;
            d.Hide();
        }

        async Task DownloadAsync()
        {
            running?.Dispose();
            running = new CancellationTokenSource();
            var token = running.Token;
            d.IsPrimaryButtonEnabled = false;
            bar.IsIndeterminate = true;
            Say("Asking GitHub for the newest build…", busy: true);
            var log = new DialogLog(d.DispatcherQueue, Show);
            FetchedUpdate? fetched;
            try
            {
                fetched = await Task.Run(() =>
                {
                    using var _ = Cancellation.Use(token);
                    // The build found when the pane offered the update may be gone: latest-build is replaced
                    // by every push, and its download address with it.
                    var check = Updater.Check(Updater.Repo(null), Updates.InstallDir(), log);
                    token.ThrowIfCancellationRequested();
                    return check.Newer ? Updater.Fetch(check, log) : null;
                }, token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                if (closed) return;
                Say("The download failed: " + ex.Message, busy: false);
                d.PrimaryButtonText = "Retry";
                d.IsPrimaryButtonEnabled = true;
                return;
            }
            if (closed)
            {
                if (fetched != null) Updater.Discard(fetched.Folder);
                return;
            }
            if (fetched == null)
            {
                Say("This sg is already the newest build.", busy: false);
                d.PrimaryButtonText = "";
                d.CloseButtonText = "Close";
                return;
            }
            ready = fetched;
            Finish();
        }

        d.PrimaryButtonClick += async (_, args) =>
        {
            // The dialog stays open and becomes the progress of what the button started.
            args.Cancel = true;
            if (ready != null) Finish();
            else await DownloadAsync();
        };
        d.Closing += (_, _) =>
        {
            closed = true;
            running?.Cancel();
            if (!finished && ready != null)
            {
                Updater.Discard(ready.Folder);
                ready = null;
            }
        };

        await d.ShowAsync();
        running?.Dispose();
        return finished ? ready : null;
    }

    /// <summary>The download's log, read by the dialog: bytes become the bar and the line under it.</summary>
    sealed class DialogLog(DispatcherQueue ui, Action<string, long, long> show) : ILog
    {
        long _shownAt;

        public void Info(string message) { }
        public void Warn(string message) => Session.Log.Warn(message);
        public void Cmd(string message) { }

        public void Progress(string label, long done, long total, string unit, string? detail)
        {
            // A chunk is 80 KB, so 61 MB reports some 760 times; the screen needs about ten a second.
            var now = Environment.TickCount64;
            if (label == "download" && done < total && now - _shownAt < 100) return;
            _shownAt = now;
            ui.TryEnqueue(() => show(label, done, total));
        }
    }
}
