using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;

namespace OpenOutlook.Desktop;

/// <summary>
/// The ribbon icons are drawings on a 32 x 32 canvas (Assets/OutlookIcons.axaml). A canvas does not scale with its box, so an icon in a 15, 18 or
/// 28 px slot was drawn at 32 px and cut off (owner 2026-10-17: the Delete group's small icons; the Zoom icon). Every icon is now a Viewbox of the
/// slot's size around the 32 px drawing.
/// </summary>
public static class OlIcon
{
    public static Control Create(IControlTemplate? template, double size, HorizontalAlignment horizontal = HorizontalAlignment.Center,
        VerticalAlignment vertical = VerticalAlignment.Center) =>
        new Viewbox
        {
            Width = size, Height = size, HorizontalAlignment = horizontal, VerticalAlignment = vertical,
            Child = new ContentControl { Template = template, Width = 32, Height = 32 },
        };

    /// <summary>The icon of that resource key, or null when the key is unknown.</summary>
    public static IControlTemplate? Template(string key) =>
        Application.Current is { } app && app.TryFindResource(key, app.ActualThemeVariant, out var t) ? t as IControlTemplate : null;
}
