using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Sg.Core;

namespace Sg.App;

/// <summary>One restore policy and one folder renderer for both archive imports and remote backups.</summary>
public sealed partial class CheckoutAppearancePreview : UserControl
{
    CheckoutConfig? _checkout;

    public CheckoutAppearancePreview() => InitializeComponent();

    public void Hide()
    {
        Visibility = Visibility.Collapsed;
        CurrentImage.Child = SavedImage.Child = null;
        _checkout = null;
    }

    public void Show(bool included, byte[]? savedIcon, string sourceName, CheckoutConfig? checkout)
    {
        if (!included) { Hide(); return; }
        _checkout = checkout;
        Visibility = Visibility.Visible;
        UpdateCurrent();
        var saved = CheckoutIcons.Preview(checkout?.Name ?? sourceName, savedIcon);
        AutomationProperties.SetAutomationId(saved, "SavedAppearanceIcon");
        AutomationProperties.SetName(saved, "Saved checkout icon");
        SavedImage.Child = saved;
        SavedKind.Text = savedIcon == null ? "Initials" : "Custom image";
        var keep = checkout?.Icon != null;
        Outcome.Text = checkout == null ? "Choose destination" : keep ? "Keep current" : "Restore saved";
        Outcome.Glyph = checkout == null ? "\uE8B7" : keep ? "\uE73E" : "\uE896";
        Outcome.Severity = checkout == null || keep ? ChipSeverity.Neutral : ChipSeverity.Attention;
        AppearanceNote.Text = checkout == null ? "Includes checkout appearance. Choose a destination to see how it will be used."
            : CheckoutIcons.RestoreDescription(true, checkout);
    }

    public void ShowResult(bool restored, string? warning)
    {
        if (Visibility != Visibility.Visible) return;
        UpdateCurrent();
        Outcome.Text = warning != null ? "Image not saved" : restored ? "Restored" : "Kept current";
        Outcome.Glyph = warning != null ? "\uE7BA" : "\uE73E";
        Outcome.Severity = warning != null ? ChipSeverity.Caution : ChipSeverity.Success;
        AppearanceNote.Text = warning != null ? "The work is available. See the result above for the icon warning."
            : restored ? "Checkout appearance restored." : "Your existing checkout appearance was kept.";
    }

    void UpdateCurrent()
    {
        CurrentName.Text = _checkout == null ? "Current" : "Current · " + _checkout.Name;
        CurrentKind.Text = _checkout == null ? "Choose a checkout" : _checkout.Icon == null ? "Automatic initials"
            : _checkout.Icon.Length == 0 ? "Chosen initials" : "Custom image";
        var icon = _checkout == null ? new FontIcon { Glyph = "\uE8B7", FontSize = 24 }
            : CheckoutIcons.Create(_checkout);
        AutomationProperties.SetAutomationId(icon, "CurrentAppearanceIcon");
        AutomationProperties.SetName(icon, "Current checkout icon");
        CurrentImage.Child = icon;
    }
}
