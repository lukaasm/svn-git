using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Sg.Core;

namespace Sg.App;

/// <summary>A validated destination link. Changing form input hides the old action until validation completes.</summary>
public sealed class DestinationAction : Button
{
    SgRoot? _root;
    TaskFollowUp? _target;
    internal Action<TaskFollowUp>? Requested { get; set; }
    public DestinationAction()
    {
        Visibility = Visibility.Collapsed;
        HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetAutomationId(this, "ExistingDestinationAction");
        Click += (_, _) =>
        {
            if (_root == null || _target == null || !ReferenceEquals(_root, Session.Root)) return;
            if (Requested != null) Requested(_target);
            else TaskNavigation.Open(_root.RootPath, _target, NavHost.Of(this));
        };
    }
    internal void Update(SgRoot? root, TaskFollowUp? target)
    {
        _root = root;
        _target = target;
        Content = target?.Label;
        AutomationProperties.SetHelpText(this, target?.Path ?? "");
        ToolTipService.SetToolTip(this, target?.Path);
        Visibility = root != null && target != null ? Visibility.Visible : Visibility.Collapsed;
    }
}
