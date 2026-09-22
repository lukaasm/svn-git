using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace Sg.App;

/// <summary>The parent owns busy gating; a child's validation and selection state stay untouched.</summary>
public sealed class TaskGate : ContentControl
{
    readonly UiRefresh _refresh;
    ContentControl? _gate;
    FrameworkElement? _child;
    string _help = "";
    bool _blocked;
    bool _explicitHelp;
    /// <summary>Set ordinary action help; this module owns busy overrides and restores the latest help afterward.</summary>
    public static void SetHelp(DependencyObject control, string defaultHelp)
    {
        var help = defaultHelp;
        for (DependencyObject? parent = control; parent != null; parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(parent))
        {
            if (parent is not TaskGate gate) continue;
            gate._help = defaultHelp;
            gate._explicitHelp = true;
            help = Session.Tasks.Blocking(Session.Root?.RootPath ?? "")?.BlockingExplanation ?? defaultHelp;
            break;
        }
        ToolTipService.SetToolTip(control, help);
        AutomationProperties.SetHelpText(control, help);
    }
    public TaskGate()
    {
        _refresh = new(DispatcherQueue, () => { if (IsLoaded) Refresh(); });
        IsTabStop = false;
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Loaded += (_, _) =>
        {
            if (_gate == null && Content is FrameworkElement child)
            {
                _child = child;
                if (!_explicitHelp) _help = AutomationProperties.GetHelpText(child);
                SetBinding(VisibilityProperty, new Binding { Source = child, Path = new PropertyPath("Visibility"), Mode = BindingMode.OneWay });
                Content = null;
                _gate = new ContentControl
                {
                    Content = child, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    VerticalContentAlignment = VerticalAlignment.Stretch, IsTabStop = false
                };
                Content = _gate;
            }
            Session.Tasks.Changed += Changed;
            Session.RootChanged += Changed;
            Changed();
        };
        Unloaded += (_, _) => { Session.Tasks.Changed -= Changed; Session.RootChanged -= Changed; };
    }
    void Changed() => _refresh.Request();
    void Refresh()
    {
        var busy = Session.Tasks.Blocking(Session.Root?.RootPath ?? "");
        if (_gate != null) _gate.IsEnabled = busy == null;
        var reason = busy?.BlockingExplanation;
        // Keep the wrapper enabled so the tooltip remains reachable over its disabled child.
        ToolTipService.SetToolTip(this, reason);
        AutomationProperties.SetHelpText(this, reason ?? "");
        if (_child != null)
        {
            if (reason != null && !_blocked && !_explicitHelp) _help = AutomationProperties.GetHelpText(_child);
            if (reason != null || _blocked) AutomationProperties.SetHelpText(_child, reason ?? _help);
            if (_explicitHelp) ToolTipService.SetToolTip(_child, reason ?? _help);
        }
        _blocked = reason != null;
    }
}

