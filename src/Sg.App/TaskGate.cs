using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Sg.App;

/// <summary>Task collisions gate the parent; validation stays with the child and ActionHint explains both.</summary>
public sealed class TaskGate : ActionHint
{
    readonly UiRefresh _refresh;
    ContentControl? _gate;
    public TaskGate()
    {
        _refresh = new(DispatcherQueue, () => { if (IsLoaded) Refresh(); });
        Loaded += (_, _) =>
        {
            Session.Tasks.StateChanged += Changed;
            Session.RootChanged += Changed;
            Changed();
        };
        Unloaded += (_, _) => { Session.Tasks.StateChanged -= Changed; Session.RootChanged -= Changed; };
    }
    protected override object Wrap(FrameworkElement child) => _gate = new ContentControl
    {
        Content = child, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch, IsTabStop = false
    };
    void Changed() => _refresh.Request();
    void Refresh()
    {
        var busy = Session.Tasks.Blocking(Session.Root?.RootPath ?? "");
        if (_gate != null) _gate.IsEnabled = busy == null;
        OverrideHelp(busy?.BlockingExplanation);
    }
}
