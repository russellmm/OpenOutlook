using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace OpenOutlook.Desktop;

/// <summary>
/// One browser-laid-out screenshot tile, with links and a selectable text overlay placed at the
/// coordinates Chrome measured.
///
/// The tile is a bitmap, so it carries no text of its own and nothing in it could be highlighted.
/// Chrome also reports where each block of text was laid out; this control turns those rectangles into
/// a selection: pressing inside one starts a selection, dragging extends it across every block the
/// drag sweeps, the chosen blocks are painted with a highlight, and Ctrl+C puts their text -- in
/// reading order -- on the clipboard.
///
/// Selection is computed from rectangles rather than delegated to <c>SelectableTextBlock</c>, because
/// Avalonia does not hit-test invisible text over the picture, so a drag started there could never
/// begin. It deliberately does not use the embedded web view either: WebKitGTK cannot composite into
/// an Avalonia window under Wayland, which is what made the reading pane appear to freeze. Everything
/// here is drawn by Avalonia, so it behaves the same on every display server.
///
/// Link buttons are added after the text layer and handle their own clicks first, so a link stays
/// clickable where the two overlap; selecting the words inside a link means dragging across the lines
/// around it.
/// </summary>
public sealed class HtmlPageView : Canvas
{
    private static readonly IBrush SelectionTint = new SolidColorBrush(Color.FromArgb(96, 74, 128, 220));

    private readonly Bitmap _bitmap;
    private readonly Image _image;
    private readonly List<(Button Button, BrowserHtmlRenderer.LinkArea Area)> _links = [];
    private readonly List<Avalonia.Rect> _blocks = [];
    private readonly List<string> _texts = [];
    private readonly List<Avalonia.Controls.Shapes.Rectangle> _highlights = [];

    private double _scale = 1;
    private bool _dragging;
    private int _anchorIndex = -1;
    private TextSelectionGeometry.Selection _selection = TextSelectionGeometry.Selection.Empty;
    private readonly Action<string> _report;

    public HtmlPageView(Bitmap bitmap, double top, IReadOnlyList<BrowserHtmlRenderer.LinkArea> links,
        Action<string> report, IReadOnlyList<BrowserHtmlRenderer.TextArea>? textAreas = null)
    {
        _bitmap = bitmap;
        _report = report;
        ClipToBounds = true;
        Focusable = true;
        _image = new Image { Source = bitmap, Stretch = Stretch.Fill };
        Children.Add(_image);

        if (textAreas is not null)
        {
            var pageBottom = top + bitmap.PixelSize.Height;
            foreach (var area in textAreas)
            {
                var y0 = Math.Max(top, area.Y);
                var y1 = Math.Min(pageBottom, area.Y + area.Height);
                if (y1 <= y0 || area.X >= bitmap.PixelSize.Width) continue;
                // Blocks are kept in page coordinates and scaled on demand, so a pointer press
                // converts into the same space the geometry helper works in.
                _blocks.Add(new Avalonia.Rect(area.X, y0 - top, area.Width, y1 - y0));
                _texts.Add(area.Text);
            }
        }

        var linksBottom = top + bitmap.PixelSize.Height;
        foreach (var link in links)
        {
            var y0 = Math.Max(top, link.Y);
            var y1 = Math.Min(linksBottom, link.Y + link.Height);
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
                catch (Exception) { _report("Could not open this link in the system browser."); }
            };
            _links.Add((button, area));
            Children.Add(button);
        }
    }

    /// <summary>Text the current selection would copy.</summary>
    public string SelectedText => _selection.Text;

    public bool HasSelection => _selection.BlockIndexes.Count > 0;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_blocks.Count == 0) return;
        var point = e.GetPosition(this);
        // The pointer arrives in scaled control space; blocks are page coordinates.
        var index = TextSelectionGeometry.BlockAt(_blocks, point.X / _scale, point.Y / _scale);
        ClearSelection();
        if (index < 0) return;
        Focus();
        _anchorIndex = index;
        _dragging = true;
        e.Pointer.Capture(this);
        Apply(TextSelectionGeometry.Select(_blocks, _texts,
            new TextSelectionGeometry.Drag(_blocks[index], _blocks[index])));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging || _anchorIndex < 0) return;
        var point = e.GetPosition(this);
        var index = TextSelectionGeometry.BlockAt(_blocks, point.X / _scale, point.Y / _scale);
        var to = index >= 0 ? _blocks[index] : new Avalonia.Rect(point.X / _scale, point.Y / _scale, 1, 1);
        Apply(TextSelectionGeometry.Select(_blocks, _texts,
            new TextSelectionGeometry.Drag(_blocks[_anchorIndex], to)));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        if (_selection.BlockIndexes.Count > 0)
            _report($"Selected {_selection.Text.Length} characters. Press Ctrl+C to copy.");
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (ctrl && e.Key == Key.A)
        {
            ClearSelection();
            Apply(TextSelectionGeometry.SelectAll(_blocks, _texts));
            _report($"Selected all {_selection.BlockIndexes.Count} text blocks. Press Ctrl+C to copy.");
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.C)
        {
            if (_selection.Text.Length == 0) return;
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) return;
            _ = CopyAsync(clipboard, _selection.Text);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && HasSelection)
        {
            ClearSelection();
            e.Handled = true;
        }
    }

    private async Task CopyAsync(Avalonia.Input.Platform.IClipboard clipboard, string text)
    {
        try
        {
            await clipboard.SetTextAsync(text);
            _report($"Copied {text.Length} characters to the clipboard.");
        }
        catch (Exception ex)
        {
            _report("Could not copy the selected text.");
        }
    }

    private void Apply(TextSelectionGeometry.Selection selection)
    {
        _selection = selection;
        foreach (var highlight in _highlights) Children.Remove(highlight);
        _highlights.Clear();
        foreach (var index in selection.BlockIndexes)
        {
            var block = _blocks[index];
            var rectangle = new Avalonia.Controls.Shapes.Rectangle
            {
                Width = block.Width * _scale,
                Height = Math.Max(1, block.Height * _scale),
                Fill = SelectionTint,
                IsHitTestVisible = false,
            };
            SetLeft(rectangle, block.X * _scale);
            SetTop(rectangle, block.Y * _scale);
            rectangle.SetValue(Panel.ZIndexProperty, 10);
            _highlights.Add(rectangle);
            Children.Add(rectangle);
        }
    }

    public void ClearSelection()
    {
        _selection = TextSelectionGeometry.Selection.Empty;
        _anchorIndex = -1;
        foreach (var highlight in _highlights) Children.Remove(highlight);
        _highlights.Clear();
    }

    public void SetScale(double scale)
    {
        _scale = Math.Max(0.01, scale);
        Width = _bitmap.PixelSize.Width * _scale;
        Height = _bitmap.PixelSize.Height * _scale;
        _image.Width = Width;
        _image.Height = Height;
        foreach (var (button, area) in _links)
        {
            SetLeft(button, area.X * _scale);
            SetTop(button, area.Y * _scale);
            button.Width = area.Width * _scale;
            button.Height = area.Height * _scale;
        }
        // The highlight has to move with the picture it describes.
        Apply(_selection);
    }
}
