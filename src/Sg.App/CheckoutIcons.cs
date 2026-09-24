using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Sg.Core;

namespace Sg.App;

/// <summary>Checkout identity uses the same theme palette as usernames, in both sidebar sizes and headers.</summary>
internal static class CheckoutIcons
{
    public static FontIcon Create(string name)
    {
        var icon = new FontIcon { Glyph = IdentityColor.Initials(name), FontFamily = new FontFamily("Segoe UI"),
            FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontSize = 12, Width = 24, Height = 24 };
        void Paint() => icon.Foreground = UserColors.Brush(name);
        icon.Loaded += (_, _) => Paint();
        icon.ActualThemeChanged += (_, _) => Paint();
        AutomationProperties.SetName(icon, name + " checkout");
        AutomationProperties.SetAutomationId(icon, "CheckoutIcon_" + name);
        ToolTipService.SetToolTip(icon, name);
        Paint();
        return icon;
    }
}
