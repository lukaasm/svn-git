using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Composition;
using System.Numerics;
using Windows.Storage.Streams;
using Sg.Core;
using Windows.UI;

namespace Sg.App;

/// <summary>
/// Checkout identity uses the same theme palette as usernames, in both sidebar sizes and headers. A mark in
/// the folder's corner says which server the checkout speaks, so an SVN working copy and a git clone of the
/// same name never read as the same thing.
/// </summary>
internal static class CheckoutIcons
{
    internal const double Size = 24;
    sealed record Identity(string? Store, string Name, string? Image);
    static Identity IdentityOf(CheckoutConfig checkout) => new(Session.Root?.StorePath, checkout.Name, checkout.Icon);
    internal static bool Matches(IconElement? icon, CheckoutConfig checkout) => Equals(icon?.Tag, IdentityOf(checkout));

    internal static string RestoreDescription(bool included, CheckoutConfig checkout) => !included ? ""
        : checkout.Icon == null ? "The saved checkout appearance will be restored."
        : "Your existing checkout appearance will be kept.";

    /// <summary>What the corner mark stands for, in words, for the tooltip and a screen reader.</summary>
    public static string KindText(CheckoutConfig checkout) => KindText(checkout.Kind);

    static string KindText(CheckoutKind kind) => kind == CheckoutKind.Git ? "git clone" : "SVN working copy";

    public static FontIcon Create(CheckoutConfig checkout)
    {
        var path = Session.Root is { } root ? CheckoutAppearance.IconPath(root, checkout) : null;
        return Create(checkout.Name, IdentityOf(checkout), path, null, checkout.Kind);
    }

    /// <summary>An image being chosen, before it belongs to a checkout: no server mark, since none is known yet.</summary>
    internal static FontIcon Preview(string name, byte[]? png) => Create(name, null, null, png, null);

    static FontIcon Create(string name, object? identity, string? path, byte[]? png, CheckoutKind? kind)
    {
        var icon = new FontIcon { Glyph = IdentityColor.Initials(name), FontFamily = new FontFamily("Segoe UI"),
            FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontSize = 11, Width = Size, Height = Size, Tag = identity };
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
            Vector2[] corners = [new(2, 21), new(2, 4), new(8, 4), new(11, 7), new(22, 7), new(22, 21), new(2, 21)];
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
            if (kind != null) visual.Children.InsertAtTop(Badge(compositor, kind.Value));
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
                    image.Size = new Vector2((float)Size, (float)Size);
                    var brush = compositor.CreateSurfaceBrush(loading);
                    brush.Stretch = CompositionStretch.Uniform;
                    image.Brush = brush;
                    // A custom image is the identity itself; reserve the folder for the initials fallback. The
                    // server mark stays, over the image's corner: which server it speaks is not in the image.
                    visual.Children.RemoveAll();
                    visual.Children.InsertAtTop(image);
                    if (kind != null) visual.Children.InsertAtTop(Badge(compositor, kind.Value));
                    icon.Glyph = "";
                    AutomationProperties.SetHelpText(icon, "Custom checkout image");
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
        // The server mark in words: the help text says what the icon shows, this says which server it speaks.
        if (kind is { } k)
        {
            AutomationProperties.SetFullDescription(icon, KindText(k));
            ToolTipService.SetToolTip(icon, name + "\n" + KindText(k));
        }
        else ToolTipService.SetToolTip(icon, name);
        Paint();
        return icon;
    }

    /// <summary>
    /// The server mark: an orange diamond for git, a blue disc for SVN. It sits in the empty corner right of
    /// the folder's tab, above the body, where it does not cover the initials; in the body's corner it hid
    /// half the second initial. Over a custom image it takes the same corner. Two shapes as well as two colours, so it reads without
    /// the colour too. The colours are the same in both themes: both carry against a light pane and a dark one.
    /// </summary>
    static ShapeVisual Badge(Compositor compositor, CheckoutKind kind)
    {
        var badge = compositor.CreateShapeVisual();
        badge.Size = new Vector2(24, 24);
        CompositionSpriteShape shape;
        if (kind == CheckoutKind.Git)
        {
            var square = compositor.CreateRectangleGeometry();
            square.Size = new Vector2(5.4f, 5.4f);
            shape = compositor.CreateSpriteShape(square);
            shape.Offset = new Vector2(16.8f, 1.2f);
            shape.CenterPoint = new Vector2(2.7f, 2.7f);
            shape.RotationAngleInDegrees = 45;
            shape.FillBrush = compositor.CreateColorBrush(Color.FromArgb(255, 0xF0, 0x50, 0x33));
        }
        else
        {
            var disc = compositor.CreateEllipseGeometry();
            disc.Center = new Vector2(19.5f, 3.9f);
            disc.Radius = new Vector2(3.4f, 3.4f);
            shape = compositor.CreateSpriteShape(disc);
            shape.FillBrush = compositor.CreateColorBrush(Color.FromArgb(255, 0x37, 0x78, 0xC2));
        }
        badge.Shapes.Add(shape);
        return badge;
    }
}
