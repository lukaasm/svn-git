using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Sg.App;

/// <summary>Keeps disabled explanations reachable by hover, keyboard and automation.
/// The child owns availability; this enabled wrapper never invokes it or changes its local state.</summary>
public class ActionHint : ContentControl
{
    FrameworkElement? _child;
    long _tipToken, _helpToken;
    string _help = "";
    string? _override;
    bool _publishing;
    readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 380 };
    readonly ToolTip _tip = new();
    public ActionHint()
    {
        IsTabStop = false;
        UseSystemFocusVisuals = true;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        _tip.Content = _text;
        _tip.PlacementTarget = this;
        Loaded += Attach;
        Unloaded += Detach;
        GotFocus += (_, _) => { if (IsTabStop && FocusState != FocusState.Unfocused) _tip.IsOpen = true; };
        LostFocus += (_, _) => _tip.IsOpen = false;
    }
    public static void SetHelp(DependencyObject control, string help)
    {
        ToolTipService.SetToolTip(control, help);
        AutomationProperties.SetHelpText(control, help);
    }
    protected virtual object Wrap(FrameworkElement child) => child;
    protected void OverrideHelp(string? reason) { _override = reason; RefreshHint(); }
    void Attach(object sender, RoutedEventArgs args)
    {
        if (_child == null && Content is FrameworkElement child)
        {
            _child = child;
            SetBinding(VisibilityProperty, new Binding { Source = child, Path = new PropertyPath("Visibility"), Mode = BindingMode.OneWay });
            Content = null;
            Content = Wrap(child);
        }
        if (_child == null) return;
        _help = TipText() ?? AutomationProperties.GetHelpText(_child);
        _tipToken = _child.RegisterPropertyChangedCallback(ToolTipService.ToolTipProperty, (_, _) => { _help = TipText() ?? _help; RefreshHint(); });
        _helpToken = _child.RegisterPropertyChangedCallback(AutomationProperties.HelpTextProperty, (_, _) =>
        {
            if (_publishing) return;
            _help = AutomationProperties.GetHelpText(_child); RefreshHint();
        });
        if (_child is Control control) control.IsEnabledChanged += EnabledChanged;
        RefreshHint();
    }
    void Detach(object sender, RoutedEventArgs args)
    {
        _tip.IsOpen = false;
        if (_child == null) return;
        _child.UnregisterPropertyChangedCallback(ToolTipService.ToolTipProperty, _tipToken);
        _child.UnregisterPropertyChangedCallback(AutomationProperties.HelpTextProperty, _helpToken);
        if (_child is Control control) control.IsEnabledChanged -= EnabledChanged;
    }
    string? TipText() => ToolTipService.GetToolTip(_child) switch { string text => text, ToolTip { Content: string text } => text, _ => null };
    void EnabledChanged(object sender, DependencyPropertyChangedEventArgs args) => RefreshHint();
    void RefreshHint()
    {
        if (_child == null) return;
        var reason = _override ?? _help;
        var unavailable = _child is Control { IsEnabled: false } && !string.IsNullOrWhiteSpace(reason);
        _text.Text = reason;
        ToolTipService.SetToolTip(this, unavailable ? _tip : null);
        IsTabStop = unavailable;
        if (!unavailable) _tip.IsOpen = false;
        var name = AutomationProperties.GetName(_child);
        if (name.Length == 0) name = _child switch { IconButton b => b.Text, Button { Content: string text } => text, Button { Content: TextBlock text } => text.Text, _ => "Action" };
        AutomationProperties.SetName(this, name + " unavailable");
        var id = AutomationProperties.GetAutomationId(_child);
        AutomationProperties.SetAutomationId(this, "DisabledHint_" + (id.Length > 0 ? id : _child.Name));
        AutomationProperties.SetHelpText(this, unavailable ? reason : "");
        _publishing = true;
        try { AutomationProperties.SetHelpText(_child, reason); }
        finally { _publishing = false; }
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new HintPeer(this);
    sealed class HintPeer(ActionHint owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(ActionHint);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
        protected override bool IsControlElementCore() => owner.IsTabStop;
        protected override bool IsKeyboardFocusableCore() => owner.IsTabStop;
        protected override bool HasKeyboardFocusCore() => owner.FocusState != FocusState.Unfocused;
        protected override void SetFocusCore() => owner.Focus(FocusState.Programmatic);
    }
}
