using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Sg.Core;

namespace Sg.App;

/// <summary>Names use the existing eight identity colors everywhere, including WebView content.</summary>
public static class UserColors
{
    public static readonly DependencyProperty NameProperty = DependencyProperty.RegisterAttached("Name", typeof(string), typeof(UserColors), new PropertyMetadata(null, Changed));
    static readonly DependencyProperty HeaderProperty = DependencyProperty.RegisterAttached("Header", typeof(object), typeof(UserColors), new PropertyMetadata(null));
    sealed record HeaderParts(string Prefix, string Suffix);
    public static string? GetName(DependencyObject target) => (string?)target.GetValue(NameProperty);
    public static void SetName(DependencyObject target, string? value) => target.SetValue(NameProperty, value);
    public static int Index(string name) => IdentityColor.Index(name);
    public static Brush Brush(string name) => (Brush)Application.Current.Resources[string.IsNullOrWhiteSpace(name) ? "TextFillColorSecondaryBrush" : "RepoBrush" + Index(name)];
    public static string Hex(Brush? brush) => brush is SolidColorBrush b ? $"{b.Color.R:x2}{b.Color.G:x2}{b.Color.B:x2}" : Themes.Current.Foreground;
    public static string[] Palette => Enumerable.Range(0, 8).Select(i => Hex((Brush)Application.Current.Resources["RepoBrush" + i])).ToArray();

    static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not TextBlock text) return;
        text.Loaded -= Loaded; text.Loaded += Loaded;
        text.ActualThemeChanged -= ThemeChanged; text.ActualThemeChanged += ThemeChanged;
        Paint(text);
    }
    static void Loaded(object sender, RoutedEventArgs args) => Paint((TextBlock)sender);
    static void ThemeChanged(FrameworkElement sender, object args) => Paint((TextBlock)sender);
    static void Paint(TextBlock text)
    {
        var name = GetName(text) ?? "";
        if (text.GetValue(HeaderProperty) is HeaderParts parts)
        {
            text.Inlines.Clear();
            text.Inlines.Add(new Run { Text = parts.Prefix });
            text.Inlines.Add(new Run { Text = name, Foreground = Brush(name) });
            text.Inlines.Add(new Run { Text = parts.Suffix });
        }
        else text.Foreground = Brush(name);
    }
    public static void Header(TextBlock text, string prefix, string name, string suffix)
    {
        text.SetValue(HeaderProperty, new HeaderParts(prefix, suffix)); SetName(text, name); Paint(text);
    }
    public static void Plain(TextBlock text, string value)
    {
        text.ClearValue(HeaderProperty); text.ClearValue(NameProperty); text.ClearValue(TextBlock.ForegroundProperty); text.Text = value;
        text.Loaded -= Loaded; text.ActualThemeChanged -= ThemeChanged;
    }
}
