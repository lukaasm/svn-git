using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Sg.Core;

namespace Sg.App;

/// <summary>Compact feedback summary, retained in place when an agent replies or resolves it.</summary>
internal sealed class InboxCard : UserControl
{
    readonly StatusChip _state = new();
    readonly TextBlock _worktree = Text(), _file = Text(), _range = Text(), _comment = Text(), _latest = Text(), _author = Text(), _warning = Text();
    readonly IconButton _open = new() { Text = "Open comment", Glyph = "\uE8A5", VerticalAlignment = VerticalAlignment.Top };
    ReviewInboxWorktree? _source;
    ReviewInboxItem? _item;
    public InboxCard(Action<ReviewInboxWorktree, ReviewInboxItem> open)
    {
        var body = new StackPanel { Spacing = 6 };
        var header = new WrapRow { Spacing = 8 }; header.Children.Add(_state); header.Children.Add(_worktree);
        var top = new Grid { ColumnSpacing = 12 };
        top.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        top.Children.Add(header); Grid.SetColumn(_open, 1); top.Children.Add(_open);
        _worktree.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _file.MaxLines = 2; _file.TextTrimming = TextTrimming.CharacterEllipsis;
        _range.Style = (Style)Application.Current.Resources["Secondary"];
        _comment.MaxLines = 2; _comment.TextTrimming = TextTrimming.CharacterEllipsis;
        _latest.MaxLines = 2; _latest.TextTrimming = TextTrimming.CharacterEllipsis;
        _author.Style = _warning.Style = (Style)Application.Current.Resources["Secondary"];
        body.Children.Add(top); body.Children.Add(_file); body.Children.Add(_range); body.Children.Add(_comment); body.Children.Add(_author); body.Children.Add(_latest); body.Children.Add(_warning);
        Content = new Border { Style = (Style)Application.Current.Resources["Card"], Padding = new Thickness(16), Child = body };
        _open.Click += (_, _) => { if (_source != null && _item != null) open(_source, _item); };
    }
    static TextBlock Text() => new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    public void Update(ReviewInboxWorktree worktree, ReviewInboxItem item)
    {
        if (_item == item && _source?.Branch == worktree.Branch && _source.Checkout == worktree.Checkout && _source.Error == worktree.Error) return;
        _source = worktree; _item = item;
        AutomationProperties.SetAutomationId(this, "ReviewInboxThread_" + item.Id);
        AutomationProperties.SetAutomationId(_open, "ReviewInboxOpen_" + item.Id);
        _state.Text = item.Conflict ? "Conflicting replies" : item.State == "open" ? "Open" : "Resolved";
        _state.Glyph = item.Conflict ? "\uE7BA" : item.State == "open" ? "\uE90A" : "\uE73E";
        _state.Severity = item.Conflict ? ChipSeverity.Caution : item.State == "open" ? ChipSeverity.Attention : ChipSeverity.Success;
        _worktree.Text = worktree.Branch + " · " + worktree.Checkout;
        var lines = item.First == 0 ? "whole file" : item.First == item.Last ? "line " + item.First : $"lines {item.First}–{item.Last}";
        _file.Text = item.File; ToolTipService.SetToolTip(_file, item.File);
        _range.Text = $"{item.Side} · {lines}";
        AutomationProperties.SetName(_open, $"Open comment in {worktree.Branch}: {item.File}, {lines}");
        ToolTipService.SetToolTip(_open, "Open this comment in code review");
        _comment.Text = item.Comment;
        UserColors.Header(_author, "", item.Author, " · commented" + (item.Updates == 0 ? $" · {item.Updated.LocalDateTime:g}" : ""));
        _latest.Visibility = item.Updates == 0 ? Visibility.Collapsed : Visibility.Visible;
        UserColors.Header(_latest, "", item.LatestActor, $" · {item.LatestAction} · {item.Updated.LocalDateTime:g}\n{item.LatestBody}");
        _warning.Text = "Showing last available feedback · Refresh to retry";
        _warning.Visibility = worktree.Error == null ? Visibility.Collapsed : Visibility.Visible;
    }
}
