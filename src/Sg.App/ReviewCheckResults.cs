using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Sg.Core;

namespace Sg.App;

/// <summary>Compact recorded results; full output is opened only when requested.</summary>
internal sealed class ReviewCheckResults : UserControl
{
    public ReviewCheckResults(ReviewRecord record, Action<int> open)
    {
        var body = new StackPanel { Spacing = 8 };
        var failed = record.Checks.Count(c => c.ExitCode != 0);
        var summary = record.Checks.Count == 0 ? "No automated checks recorded" :
            $"{record.Checks.Count - failed} passed · {failed} failed · {record.Checks.Sum(c => c.Seconds):F1}s total";
        var heading = new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(heading, "ReviewCheckSummary");
        body.Children.Add(heading);
        for (var i = 0; i < record.Checks.Count; i++)
        {
            var index = i;
            var check = record.Checks[i];
            var name = DisplayName(check, i);
            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(12) };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var text = new StackPanel { Spacing = 4 };
            text.Children.Add(new TextBlock { Text = name, TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            text.Children.Add(new StatusChip {
                Text = check.ExitCode == 0 ? $"Passed · {check.Seconds:F1}s" : $"Failed · exit {check.ExitCode} · {check.Seconds:F1}s",
                Severity = check.ExitCode == 0 ? ChipSeverity.Success : ChipSeverity.Critical,
                Glyph = check.ExitCode == 0 ? "\uE73E" : "\uEA39"
            });
            row.Children.Add(text);
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            label.Children.Add(new FontIcon { Glyph = "\uE8A5", FontSize = 14 });
            label.Children.Add(new TextBlock { Text = "View output" });
            var link = new HyperlinkButton { Content = label, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(link, "View output · " + name);
            AutomationProperties.SetAutomationId(link, $"ReviewCheckOutput_{i}");
            link.Click += (_, _) => DispatcherQueue.TryEnqueue(() => open(index));
            Grid.SetColumn(link, 1);
            row.Children.Add(link);
            body.Children.Add(new Border {
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                CornerRadius = new CornerRadius(4), Child = row
            });
        }
        Content = body;
    }

    internal static string DisplayName(ReviewCheckResult check, int index) =>
        string.IsNullOrWhiteSpace(check.Name) ? $"Check {index + 1}" : check.Name;
}

internal sealed class ReviewCheckOutputPage : WorkflowPage
{
    readonly ReviewRecord _record;
    readonly ReviewCheckResult _check;
    readonly SearchableOutput _output;
    public ReviewCheckOutputPage(ReviewRecord record, int index) : base("Check output")
    {
        _record = record;
        _check = record.Checks[index];
        _output = new SearchableOutput(_check.Output);
        Subtitle = ReviewCheckResults.DisplayName(_check, index);
        Shortcuts.Add(this, Windows.System.VirtualKey.F, Windows.System.VirtualKeyModifiers.Control, _output.FocusSearch);
    }
    protected override Task Reload()
    {
        Body.Children.Clear();
        Text(Subtitle, true);
        Status(_check.ExitCode == 0 ? "Passed" : $"Failed · exit code {_check.ExitCode}",
            _check.ExitCode == 0 ? ChipSeverity.Success : ChipSeverity.Critical, _check.ExitCode == 0 ? "\uE73E" : "\uEA39");
        Text($"Recorded {_record.Checked.LocalDateTime:g} · {_check.Seconds:F1}s · branch {_record.Branch}");
        Text("Saved result from this check run. Return to Review readiness to check whether it still matches the current version.");
        Details("Command", [_check.Command]);
        if (string.IsNullOrWhiteSpace(_check.Output)) Text("No output was captured.");
        Body.Children.Add(_output);
        return Task.CompletedTask;
    }
}
