using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Sg.Core;

namespace Sg.App;

/// <summary>
/// TaskGate for the items of a menu, which nothing can wrap. An item marked Gated opens greyed while a
/// task holds the root, and the top line of the menu names that task: a disabled item shows no tooltip,
/// so the reason is written where the eye already is. Closing the menu gives each item back only what
/// the gate took, so a page's own rules for the item stay the page's.
/// </summary>
public static class MenuGate
{
    public static readonly DependencyProperty GatedProperty = DependencyProperty.RegisterAttached(
        "Gated", typeof(bool), typeof(MenuGate), new PropertyMetadata(false));
    public static bool GetGated(DependencyObject item) => (bool)item.GetValue(GatedProperty);
    public static void SetGated(DependencyObject item, bool value) => item.SetValue(GatedProperty, value);

    /// <summary>On a MenuFlyout or a CommandBar whose items carry Gated.</summary>
    public static readonly DependencyProperty WatchProperty = DependencyProperty.RegisterAttached(
        "Watch", typeof(bool), typeof(MenuGate), new PropertyMetadata(false, (o, e) => { if (e.NewValue is true) Hook(o); }));
    public static bool GetWatch(DependencyObject owner) => (bool)owner.GetValue(WatchProperty);
    public static void SetWatch(DependencyObject owner, bool value) => owner.SetValue(WatchProperty, value);

    static readonly DependencyProperty TakenProperty = DependencyProperty.RegisterAttached(
        "Taken", typeof(bool), typeof(MenuGate), new PropertyMetadata(false));
    const string NoticeTag = "MenuGate.Notice";

    /// <summary>A menu built in code: gate these items of it.</summary>
    public static MenuFlyout Gate(MenuFlyout menu, params MenuFlyoutItemBase[] items)
    {
        foreach (var item in items) SetGated(item, true);
        SetWatch(menu, true);
        return menu;
    }

    static void Hook(DependencyObject owner)
    {
        switch (owner)
        {
            case MenuFlyout menu:
                menu.Opening += (_, _) => Take(menu.Items, Flatten(menu.Items), busy => [
                    new MenuFlyoutItem { Text = Line(busy), Icon = new FontIcon { Glyph = "" }, IsEnabled = false, Tag = NoticeTag },
                    new MenuFlyoutSeparator { Tag = NoticeTag }]);
                menu.Closed += (_, _) => GiveBack(menu.Items, Flatten(menu.Items));
                break;
            case CommandBar bar:
                bar.Opening += (_, _) => Take(bar.SecondaryCommands, bar.SecondaryCommands.OfType<Control>(), busy => [
                    new AppBarButton { Label = Line(busy), Icon = new FontIcon { Glyph = "" }, IsEnabled = false, Tag = NoticeTag },
                    new AppBarSeparator { Tag = NoticeTag }]);
                bar.Closed += (_, _) => GiveBack(bar.SecondaryCommands, bar.SecondaryCommands.OfType<Control>());
                break;
        }
    }

    static string Line(TaskSnapshot busy) => busy.Stopping ? busy.Title + " is stopping" : "Wait for " + busy.Title;

    static void Take<T>(IList<T> items, IEnumerable<Control> all, Func<TaskSnapshot, T[]> notice) where T : class
    {
        GiveBack(items, all);
        var busy = Session.Tasks.Blocking(Session.Root?.RootPath ?? "");
        if (busy == null) return;
        var took = false;
        foreach (var item in all.ToList())
        {
            if (!GetGated(item) || !item.IsEnabled) continue;
            item.IsEnabled = false;
            item.SetValue(TakenProperty, true);
            took = true;
        }
        if (!took) return;
        var lines = notice(busy);
        if (lines[0] is DependencyObject first) AutomationProperties.SetHelpText(first, busy.BlockingExplanation);
        for (var i = 0; i < lines.Length; i++) items.Insert(i, lines[i]);
    }

    static void GiveBack<T>(IList<T> items, IEnumerable<Control> all) where T : class
    {
        for (var i = items.Count - 1; i >= 0; i--)
            if (items[i] is FrameworkElement { Tag: NoticeTag }) items.RemoveAt(i);
        foreach (var item in all.ToList())
        {
            if (!(bool)item.GetValue(TakenProperty)) continue;
            item.ClearValue(TakenProperty);
            item.IsEnabled = true;
        }
    }

    static IEnumerable<Control> Flatten(IList<MenuFlyoutItemBase> items)
    {
        foreach (var item in items)
        {
            yield return item;
            if (item is MenuFlyoutSubItem sub)
                foreach (var inner in Flatten(sub.Items)) yield return inner;
        }
    }
}
