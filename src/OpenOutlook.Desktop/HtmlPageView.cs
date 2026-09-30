using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace OpenOutlook.Desktop;

/// <summary>One browser-laid-out screenshot tile with clickable links at measured browser coordinates.</summary>
public sealed class HtmlPageView : Canvas
{
    private readonly Bitmap _bitmap;
    private readonly Image _image;
    private readonly List<(Button Button, BrowserHtmlRenderer.LinkArea Area)> _links = [];

    public HtmlPageView(Bitmap bitmap, double top, IReadOnlyList<BrowserHtmlRenderer.LinkArea> links,
        Action<string> report)
    {
        _bitmap = bitmap;
        ClipToBounds = true;
        _image = new Image { Source = bitmap, Stretch = Stretch.Fill };
        Children.Add(_image);
        var bottom = top + bitmap.PixelSize.Height;
        foreach (var link in links)
        {
            var y0 = Math.Max(top, link.Y);
            var y1 = Math.Min(bottom, link.Y + link.Height);
            if (y1 <= y0 || link.X >= bitmap.PixelSize.Width) continue;
            var area = link with { Y = y0 - top, Height = y1 - y0 };
            var button = new Button
            {
                Background = Brushes.Transparent, BorderThickness = new Avalonia.Thickness(0),
                Padding = new Avalonia.Thickness(0), Opacity = 0.02,
                [ToolTip.TipProperty] = link.Url
            };
            button.Click += (_, _) =>
            {
                if (!SafeHtmlDocument.TryLink(link.Url, out var url)) return;
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch (Exception) { report("Could not open this link in the system browser."); }
            };
            _links.Add((button, area));
            Children.Add(button);
        }
    }

    public void SetScale(double scale)
    {
        Width = _bitmap.PixelSize.Width * scale;
        Height = _bitmap.PixelSize.Height * scale;
        _image.Width = Width;
        _image.Height = Height;
        foreach (var (button, area) in _links)
        {
            SetLeft(button, area.X * scale);
            SetTop(button, area.Y * scale);
            button.Width = area.Width * scale;
            button.Height = area.Height * scale;
        }
    }
}
