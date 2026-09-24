using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Sg.Core;

namespace Sg.App;

/// <summary>One composer owns draft persistence, discard and retry for comments and thread responses.</summary>
internal static class ReviewComposer
{
    internal sealed record Options(string Title, string Submit, string Header, string File, string Version,
        ReviewFile? Code = null, DiffView.LineRange? Selection = null, bool CanSubmit = true, string Side = "modified");

    public static async Task<bool> Show(XamlRoot xamlRoot, ReviewDrafts store, string scope, string key, Options options, Func<ReviewDraft, Task> submit)
    {
        var saved = await Task.Run(() => store.Read(scope, key));
        var draft = saved.Draft ?? new(Guid.NewGuid().ToString("N"), options.File, "", options.Side, options.Selection?.First ?? 0, options.Selection?.Last ?? 0, options.Version);
        var side = new ComboBox { Header = "Code version", ItemsSource = new[] { "modified", "original" }, SelectedIndex = draft.Side == "original" ? 1 : 0 };
        var first = new NumberBox { Header = "First line (0 for whole file)", Value = draft.First ?? double.NaN, Minimum = 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var last = new NumberBox { Header = "Last line", Value = draft.Last ?? double.NaN, Minimum = 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var body = new TextBox { Header = options.Header, Text = draft.Body, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 100, MaxLength = 32000 };
        var status = new TextBlock { Text = saved.Draft == null ? "Drafts save automatically on this device." : "Draft restored from this device.", TextWrapping = TextWrapping.Wrap };
        var error = new InfoBar { IsOpen = false, IsClosable = false, Severity = InfoBarSeverity.Error };
        var changed = draft.SourceVersion != options.Version;
        var isComment = key.StartsWith("comment:", StringComparison.Ordinal);
        var confirm = new CheckBox { Content = "I checked this feedback against the current code", Visibility = changed ? Visibility.Visible : Visibility.Collapsed };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = options.File, TextWrapping = TextWrapping.Wrap });
        if (isComment) { content.Children.Add(side); content.Children.Add(first); content.Children.Add(last); }
        if (!options.CanSubmit) content.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Message = "This file could not be loaded. You can keep, copy or discard your draft. Reload the file before submitting." });
        if (changed) content.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Message = "Code changed since this draft. Review its text and line range before submitting." });
        content.Children.Add(body); content.Children.Add(confirm); content.Children.Add(status); content.Children.Add(error);
        AutomationProperties.SetAutomationId(body, isComment ? "ReviewCommentBody" : "ReviewAddressBody");
        AutomationProperties.SetAutomationId(first, "ReviewCommentFirst"); AutomationProperties.SetAutomationId(last, "ReviewCommentLast");
        AutomationProperties.SetAutomationId(side, "ReviewCommentSide"); AutomationProperties.SetAutomationId(confirm, "ReviewDraftConfirmCode");
        AutomationProperties.SetAutomationId(status, "ReviewDraftStatus"); AutomationProperties.SetAutomationId(error, "ReviewDraftError");
        var dialog = new ContentDialog { XamlRoot = xamlRoot, Title = options.Title, Content = content,
            PrimaryButtonText = options.Submit, SecondaryButtonText = "Discard draft", CloseButtonText = "Keep draft", DefaultButton = ContentDialogButton.Primary };
        var lineCounts = options.Code == null ? null : new[] { options.Code.Modified.Count(c => c == '\n') + 1, options.Code.Original.Count(c => c == '\n') + 1 };
        var timer = DispatcherQueue.GetForCurrentThread().CreateTimer(); timer.Interval = TimeSpan.FromMilliseconds(350); timer.IsRepeating = false;
        Task<bool> pending = Task.FromResult(true);
        bool busy = false, published = false, completed = false, closeSaved = false;
        int edit = 0;
        ReviewDraft Capture() => draft with { Body = body.Text, Side = (string)side.SelectedItem,
            First = double.IsFinite(first.Value) ? first.Value : null, Last = double.IsFinite(last.Value) ? last.Value : null,
            SourceVersion = !changed || confirm.IsChecked == true ? options.Version : draft.SourceVersion };
        void Validate()
        {
            var range = lineCounts == null || double.IsFinite(first.Value) && double.IsFinite(last.Value)
                && first.Value == Math.Truncate(first.Value) && last.Value == Math.Truncate(last.Value)
                && first.Value >= 0 && last.Value >= first.Value && last.Value <= lineCounts[side.SelectedIndex] && (first.Value > 0 || last.Value == 0);
            dialog.IsPrimaryButtonEnabled = !busy && (published || options.CanSubmit && !string.IsNullOrWhiteSpace(body.Text) && range && (!changed || confirm.IsChecked == true));
            dialog.IsSecondaryButtonEnabled = !busy && !published;
        }
        async Task<bool> Persist(Task<bool> previous, ReviewDraft? value, int revision)
        {
            await previous;
            try
            {
                if (saved.Draft != value) saved = await Task.Run(() => store.Write(scope, key, value, saved.Revision));
                if (edit == revision) { status.Text = value == null ? "Draft discarded." : "Draft saved on this device."; error.IsOpen = false; }
                return true;
            }
            catch (Exception e) { error.Message = e.Message; error.IsOpen = true; status.Text = "Draft not saved. Your text is still here."; return false; }
        }
        Task<bool> Save(ReviewDraft? value) => pending = Persist(pending, value, edit);
        void Changed()
        {
            ++edit; status.Text = "Saving draft…"; timer.Stop(); timer.Start(); Validate();
        }
        timer.Tick += async (_, _) => await Save(Capture());
        body.TextChanged += (_, _) => Changed(); first.ValueChanged += (_, _) => Changed(); last.ValueChanged += (_, _) => Changed();
        side.SelectionChanged += (_, _) => Changed(); confirm.Checked += (_, _) => Changed(); confirm.Unchecked += (_, _) => Changed();
        void Busy(bool value)
        {
            busy = value; body.IsEnabled = side.IsEnabled = first.IsEnabled = last.IsEnabled = confirm.IsEnabled = !value && !published; Validate();
        }
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral(); timer.Stop(); Busy(true); args.Cancel = true;
            try
            {
                var value = Capture();
                if (!published)
                {
                    if (!await Save(value)) return;
                    await submit(value); published = true;
                }
                if (!await Save(null)) { dialog.PrimaryButtonText = "Finish"; return; }
                completed = closeSaved = true; args.Cancel = false;
            }
            catch (Exception e) { error.Message = e.Message; error.IsOpen = true; }
            finally { Busy(false); deferral.Complete(); }
        };
        dialog.SecondaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral(); timer.Stop(); Busy(true);
            try { closeSaved = await Save(null); args.Cancel = !closeSaved; }
            finally { Busy(false); deferral.Complete(); }
        };
        // Closing also covers Escape; flushing here prevents a fast dismissal losing the debounce tail.
        dialog.Closing += async (_, args) =>
        {
            if (closeSaved) return;
            if (busy) { args.Cancel = true; return; }
            var deferral = args.GetDeferral(); timer.Stop(); Busy(true);
            try { closeSaved = await Save(published ? null : Capture()); args.Cancel = !closeSaved; completed = published && closeSaved; }
            finally { Busy(false); deferral.Complete(); }
        };
        Validate();
        try { await dialog.ShowAsync(); }
        finally { timer.Stop(); await pending; }
        return completed;
    }
}
