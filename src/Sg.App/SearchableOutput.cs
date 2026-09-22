using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Sg.App;

/// <summary>Literal, case-insensitive search over saved output, without changing its contents.</summary>
internal sealed class SearchableOutput : UserControl
{
    readonly TextBox _query = new() { PlaceholderText = "Find in output", Width = 260 };
    readonly TextBox _output;
    readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly IconButton _previous = new() { Text = "Previous", Glyph = "\uE70E", IsEnabled = false };
    readonly IconButton _next = new() { Text = "Next", Glyph = "\uE70D", IsEnabled = false };
    readonly List<int> _matches = new();
    int _current = -1;

    public SearchableOutput(string output)
    {
        _output = new TextBox {
            IsReadOnly = true, AcceptsReturn = true, Text = output, TextWrapping = TextWrapping.NoWrap,
            Height = 380, FontFamily = new FontFamily("Consolas"),
            SelectionHighlightColorWhenNotFocused = (SolidColorBrush)Application.Current.Resources["SystemControlHighlightAccentBrush"]
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_output, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_output, ScrollBarVisibility.Auto);
        AutomationProperties.SetName(_query, "Find in output");
        AutomationProperties.SetAutomationId(_query, "OutputSearch");
        AutomationProperties.SetAutomationId(_previous, "OutputPreviousMatch");
        AutomationProperties.SetAutomationId(_next, "OutputNextMatch");
        AutomationProperties.SetAutomationId(_status, "OutputMatchStatus");
        AutomationProperties.SetLiveSetting(_status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        AutomationProperties.SetName(_output, "Recorded check output");
        AutomationProperties.SetAutomationId(_output, "ReviewCheckOutputText");
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        toolbar.Children.Add(new FontIcon { Glyph = "\uE721", FontSize = 16 });
        toolbar.Children.Add(_query);
        toolbar.Children.Add(_previous);
        toolbar.Children.Add(_next);
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(toolbar);
        body.Children.Add(_status);
        body.Children.Add(_output);
        Content = body;
        _query.TextChanged += (_, _) => Search();
        _previous.Click += (_, _) => Move(-1);
        _next.Click += (_, _) => Move(1);
        _query.KeyDown += (_, e) => {
            if (e.Key != VirtualKey.Enter) return;
            var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            Move(shift ? -1 : 1); e.Handled = true;
        };
        Search();
    }

    public void FocusSearch() { _query.Focus(FocusState.Programmatic); _query.SelectAll(); }

    void Search()
    {
        _matches.Clear();
        var query = _query.Text;
        var text = _output.Text;
        if (query.Length > 0)
            for (var start = 0; start <= text.Length - query.Length;) {
                var found = text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
                if (found < 0) break;
                _matches.Add(found); start = found + query.Length;
            }
        _current = _matches.Count == 0 ? -1 : 0;
        _previous.IsEnabled = _next.IsEnabled = _matches.Count > 0;
        ShowMatch();
    }
    void Move(int direction)
    {
        if (_matches.Count == 0) return;
        _current = (_current + direction + _matches.Count) % _matches.Count;
        ShowMatch();
    }
    void ShowMatch()
    {
        if (_current < 0) {
            _output.Select(0, 0);
            _status.Text = _query.Text.Length == 0 ? "Type to search · Enter / Shift+Enter to navigate" : "No matches";
            return;
        }
        var offset = _matches[_current];
        _output.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        _output.Select(offset, _query.Text.Length);
        if (FindScroll(_output) is { } scroll) {
            var bounds = _output.GetRectFromCharacterIndex(offset, false);
            scroll.ChangeView(Math.Max(0, scroll.HorizontalOffset + bounds.X - scroll.ViewportWidth / 2),
                Math.Max(0, scroll.VerticalOffset + bounds.Y - scroll.ViewportHeight / 2), null, true);
        }
        var line = 1;
        var text = _output.Text;
        for (var i = 0; i < offset; i++)
            if (text[i] == '\n' || (text[i] == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n'))) line++;
        _status.Text = $"Match {_current + 1} of {_matches.Count} · line {line}";
    }

    static ScrollViewer? FindScroll(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer scroll) return scroll;
            if (FindScroll(child) is { } nested) return nested;
        }
        return null;
    }
}
