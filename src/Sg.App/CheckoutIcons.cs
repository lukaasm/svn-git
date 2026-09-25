using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Composition;
using System.Numerics;
using Windows.Storage.Streams;
using Sg.Core;

namespace Sg.App;

/// <summary>Checkout identity uses the same theme palette as usernames, in both sidebar sizes and headers.</summary>
internal static class CheckoutIcons
{
    internal const double Size = 32;
    sealed record Identity(string? Store, string Name, string? Image);
    static Identity IdentityOf(CheckoutConfig checkout) => new(Session.Root?.StorePath, checkout.Name, checkout.Icon);
    internal static bool Matches(IconElement? icon, CheckoutConfig checkout) => Equals(icon?.Tag, IdentityOf(checkout));

    internal static string RestoreDescription(bool included, CheckoutConfig checkout) => !included ? ""
        : checkout.Icon == null ? "The saved checkout appearance will be restored."
        : "Your existing checkout appearance will be kept.";

    public static FontIcon Create(CheckoutConfig checkout)
    {
        var path = Session.Root is { } root ? CheckoutAppearance.IconPath(root, checkout) : null;
        return Create(checkout.Name, IdentityOf(checkout), path, null);
    }

    internal static FontIcon Preview(string name, byte[]? png) => Create(name, null, null, png);

    static FontIcon Create(string name, object? identity, string? path, byte[]? png)
    {
        var icon = new FontIcon { Glyph = IdentityColor.Initials(name), FontFamily = new FontFamily("Segoe UI"),
            FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontSize = 14, Width = Size, Height = Size, Tag = identity };
        CompositionColorBrush? stroke = null;
        ContainerVisual? visual = null;
        LoadedImageSurface? surface = null;
        IRandomAccessStream? stream = null;
        void Paint()
        {
            icon.Foreground = UserColors.Brush(name);
            if (stroke != null && icon.Foreground is SolidColorBrush color) stroke.Color = color.Color;
        }
        void Unload()
        {
            ElementCompositionPreview.SetElementChildVisual(icon, null);
            surface?.Dispose(); surface = null;
            stream?.Dispose(); stream = null;
            visual?.Dispose(); visual = null;
            stroke?.Dispose(); stroke = null;
        }
        icon.Loaded += (_, _) =>
        {
            Unload();
            icon.Glyph = IdentityColor.Initials(name);
            AutomationProperties.SetHelpText(icon, "Folder with " + IdentityColor.Initials(name) + " initials");
            var compositor = ElementCompositionPreview.GetElementVisual(icon).Compositor;
            visual = compositor.CreateContainerVisual();
            stroke = compositor.CreateColorBrush();
            Paint();
            // Keep a folder silhouette in both NavigationView modes; initials sit inside its body.
            var outline = compositor.CreateShapeVisual();
            outline.Size = new Vector2((float)Size, (float)Size);
            Vector2[] corners = [new(1, 31), new(1, 2), new(10, 2), new(14, 6), new(31, 6), new(31, 31), new(1, 31)];
            for (var i = 1; i < corners.Length; i++)
            {
                var line = compositor.CreateLineGeometry();
                line.Start = corners[i - 1]; line.End = corners[i];
                var shape = compositor.CreateSpriteShape(line);
                shape.StrokeBrush = stroke;
                shape.StrokeThickness = 1.5f;
                shape.StrokeStartCap = shape.StrokeEndCap = CompositionStrokeCap.Round;
                outline.Shapes.Add(shape);
            }
            visual.Children.InsertAtTop(outline);
            ElementCompositionPreview.SetElementChildVisual(icon, visual);
            if (png == null && (path == null || !File.Exists(path))) return;
            try
            {
                // Preview saved images without publishing them into the checkout's icon store.
                if (png != null) stream = new MemoryStream(png, writable: false).AsRandomAccessStream();
                var loading = stream != null ? LoadedImageSurface.StartLoadFromStream(stream)
                    : LoadedImageSurface.StartLoadFromUri(new Uri(path!));
                surface = loading;
                loading.LoadCompleted += (_, args) =>
                {
                    if (surface != loading || visual == null || args.Status != LoadedImageSourceLoadStatus.Success) return;
                    var image = compositor.CreateSpriteVisual();
                    image.Size = new Vector2(26, 24);
                    image.Offset = new Vector3(3, 6.5f, 0);
                    var brush = compositor.CreateSurfaceBrush(loading);
                    brush.Stretch = CompositionStretch.Uniform;
                    image.Brush = brush;
                    visual.Children.InsertAtBottom(image);
                    icon.Glyph = "";
                    AutomationProperties.SetHelpText(icon, "Folder with a custom checkout image");
                };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            { /* A missing or unreadable custom image keeps the initials. */ }
        };
        icon.Unloaded += (_, _) => Unload();
        icon.ActualThemeChanged += (_, _) => Paint();
        AutomationProperties.SetName(icon, name + " checkout");
        AutomationProperties.SetAutomationId(icon, "CheckoutIcon_" + name);
        AutomationProperties.SetHelpText(icon, "Folder with " + IdentityColor.Initials(name) + " initials");
        ToolTipService.SetToolTip(icon, name);
        Paint();
        return icon;
    }
}
