using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;

namespace Sg.App;

/// <summary>A shared loading surface: real progress text and placeholders until content arrives.</summary>
public sealed class ReadFeedback : UserControl
{
    readonly TextBlock _label = new() { TextWrapping = TextWrapping.Wrap };
    readonly ProgressBar _bar = new() { IsIndeterminate = false };
    readonly Skeleton _skeleton = new() { RowCount = 3 };
    public string StateId { set => AutomationProperties.SetAutomationId(_label, value); }

    public ReadFeedback()
    {
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(_label); content.Children.Add(_bar); content.Children.Add(_skeleton);
        Content = content;
        AutomationProperties.SetLiveSetting(_label, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        Visibility = Visibility.Collapsed;
    }

    public void Show(string message, bool placeholders = true)
    {
        _label.Text = message;
        _bar.IsIndeterminate = true;
        _skeleton.Visibility = placeholders ? Visibility.Visible : Visibility.Collapsed;
        Visibility = Visibility.Visible;
    }

    public void Hide()
    {
        _bar.IsIndeterminate = false;
        _skeleton.Hide();
        Visibility = Visibility.Collapsed;
    }
}
