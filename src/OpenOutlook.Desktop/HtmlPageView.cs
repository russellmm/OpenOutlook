using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace OpenOutlook.Desktop;

/// <summary>
/// One browser-laid-out screenshot tile, with links and a selectable text layer placed at the coordinates Chrome measured.
///
/// The tile is a bitmap, so it carries no text of its own. Chrome also reports where every character was laid out
/// (<see cref="BrowserHtmlRenderer.TextRun"/>); this control turns a press, drag, double-click (word), triple-click (paragraph),
/// Shift+click (extend) or Ctrl+A into a character-level selection held by a <see cref="SnapshotSelection"/> shared by every tile of
/// the message, paints the highlight over its own part of the picture, and copies with Ctrl+C or the right-click menu -- the
/// way Outlook and Thunderbird select text in an HTML message.
///
/// Selection is computed from measured geometry rather than delegated to <c>SelectableTextBlock</c> (Avalonia does not hit-test
/// invisible text over the picture) or the embedded web view (WebKitGTK cannot composite into an Avalonia window under Wayland).
/// Everything here is drawn by Avalonia, so it behaves the same on every display server.
///
/// Link buttons are added after the text layer and handle their own clicks first, so a link stays clickable; selecting the words
/// inside a link means starting the drag beside it.
/// </summary>
public sealed class HtmlPageView : Canvas
{
    private static readonly IBrush SelectionTint = new SolidColorBrush(Color.FromArgb(96, 74, 128, 220));

    private enum Granularity { Character, Word, Paragraph }

    private readonly Bitmap _bitmap;
    private readonly Image _image;
    private readonly HighlightLayer _layer;
    private readonly List<(Button Button, BrowserHtmlRenderer.LinkArea Area)> _links = [];
    private readonly SnapshotSelection _selection;
    private readonly double _top;
    private readonly Action<string> _report;

    private double _scale = 1;
    private readonly double _pixelScale;

    /// <summary>The tile's size in CSS pixels.</summary>
    public Avalonia.Size CssSize => new(_bitmap.PixelSize.Width / _pixelScale, _bitmap.PixelSize.Height / _pixelScale);
    private bool _dragging;
    private Granularity _granularity;
    private (int Start, int End) _anchorRange;
    private bool? _overText;

    /// <summary>
    /// One tile of a rendered message. <paramref name="pixelScale"/> is how many device pixels the tile has per CSS pixel (the window's scale when it was
    /// taken, <see cref="BrowserHtmlRenderer.RenderedDocument.Scale"/>): the view is laid out in CSS pixels (<see cref="CssSize"/>; links and selection
    /// are in CSS pixels) and the picture is scaled into it, so a HiDPI screen gets every device pixel. (A bitmap tagged with a higher DPI instead is
    /// drawn cropped, pixels 1:1, by Avalonia's Image - 0.1.86.)
    /// </summary>
    public HtmlPageView(Bitmap bitmap, double top, IReadOnlyList<BrowserHtmlRenderer.LinkArea> links,
        Action<string> report, SnapshotSelection? selection = null, double pixelScale = 1)
    {
        _bitmap = bitmap;
        _pixelScale = double.IsFinite(pixelScale) && pixelScale > 0 ? pixelScale : 1;
        _top = top;
        _report = report;
        _selection = selection ?? new SnapshotSelection(SnapshotText.Empty);
        ClipToBounds = true;
        Focusable = true;
        _image = new Image { Source = bitmap, Stretch = Stretch.Fill };
        Children.Add(_image);
        _layer = new HighlightLayer(this) { IsHitTestVisible = false };
        _layer.SetValue(Panel.ZIndexProperty, 10);
        Children.Add(_layer);

        var linksBottom = top + CssSize.Height;
        foreach (var link in links)
        {
            var y0 = Math.Max(top, link.Y);
            var y1 = Math.Min(linksBottom, link.Y + link.Height);
            if (y1 <= y0 || link.X >= CssSize.Width) continue;
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

        if (!_selection.Text.IsEmpty)
        {
            var copy = new MenuItem { Header = "_Copy", InputGesture = new KeyGesture(Key.C, KeyModifiers.Control) };
            copy.Click += (_, _) => CopySelection();
            var selectAll = new MenuItem { Header = "Select _All", InputGesture = new KeyGesture(Key.A, KeyModifiers.Control) };
            selectAll.Click += (_, _) => SelectAll();
            var menu = new ContextMenu { ItemsSource = new object[] { copy, selectAll } };
            menu.Opening += (_, _) => copy.IsEnabled = !_selection.IsEmpty;
            ContextMenu = menu;
        }
    }

    /// <summary>Text the current selection would copy.</summary>
    public string SelectedText => _selection.SelectedText;

    public bool HasSelection => !_selection.IsEmpty;

    /// <summary>The selection this tile shares with the other tiles of its message.</summary>
    public SnapshotSelection Selection => _selection;

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _selection.Changed += _layer.InvalidateVisual;
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _selection.Changed -= _layer.InvalidateVisual;
    }

    /// <summary>A point in this control (scaled) as page coordinates of the whole message.</summary>
    private (double X, double Y) ToPage(Avalonia.Point point) => (point.X / _scale, _top + point.Y / _scale);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var text = _selection.Text;
        if (text.IsEmpty) return;
        var properties = e.GetCurrentPoint(this).Properties;
        if (!properties.IsLeftButtonPressed)
        {
            Focus();             // a right-click keeps the selection for the Copy menu
            return;
        }
        var (x, y) = ToPage(e.GetPosition(this));
        var caret = text.CaretAt(x, y);
        if (caret < 0) return;
        Focus();
        if (e.ClickCount >= 3)
        {
            _granularity = Granularity.Paragraph;
            _anchorRange = text.BlockAt(caret);
        }
        else if (e.ClickCount == 2)
        {
            _granularity = Granularity.Word;
            _anchorRange = text.WordAt(caret);
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _selection.Anchor >= 0)
        {
            _granularity = Granularity.Character;
            _anchorRange = (_selection.Anchor, _selection.Anchor);
        }
        else
        {
            _granularity = Granularity.Character;
            _anchorRange = (caret, caret);
        }
        Extend(caret);
        _dragging = true;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var text = _selection.Text;
        if (text.IsEmpty) return;
        var (x, y) = ToPage(e.GetPosition(this));
        if (!_dragging)
        {
            var over = text.IsOverText(x, y);
            if (over != _overText)
            {
                _overText = over;
                try { Cursor = over ? new Cursor(StandardCursorType.Ibeam) : Cursor.Default; }
                catch (Exception) { /* A platform without cursors (headless tests) keeps the default. */ }
            }
            return;
        }
        Extend(text.CaretAt(x, y));
        AutoScroll(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        if (!_selection.IsEmpty)
            _report($"Selected {_selection.SelectedText.Length} characters. Press Ctrl+C to copy.");
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
    }

    /// <summary>Moves the selection's focus to a caret, keeping the anchor's word or paragraph whole after a double or triple click.</summary>
    private void Extend(int caret)
    {
        if (caret < 0) return;
        (int Start, int End) focus = _granularity switch
        {
            Granularity.Word => _selection.Text.WordAt(caret),
            Granularity.Paragraph => _selection.Text.BlockAt(caret),
            _ => (caret, caret)
        };
        if (focus.Start < _anchorRange.Start) _selection.Set(_anchorRange.End, focus.Start);
        else _selection.Set(_anchorRange.Start, Math.Max(focus.End, _anchorRange.End));
    }

    /// <summary>Scrolls the reading pane when a drag goes past its top or bottom edge, so a selection can grow past what is visible.</summary>
    private void AutoScroll(PointerEventArgs e)
    {
        if (this.FindAncestorOfType<ScrollViewer>() is not { } viewer) return;
        var y = e.GetPosition(viewer).Y;
        var delta = y < 0 ? y : y > viewer.Bounds.Height ? y - viewer.Bounds.Height : 0;
        if (delta == 0) return;
        delta = Math.Clamp(delta, -60, 60);
        var maxY = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
        viewer.Offset = viewer.Offset.WithY(Math.Clamp(viewer.Offset.Y + delta, 0, maxY));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (ctrl && e.Key == Key.A)
        {
            SelectAll();
            e.Handled = true;
            return;
        }
        if (ctrl && (e.Key == Key.C || e.Key == Key.Insert))
        {
            if (_selection.IsEmpty) return;
            CopySelection();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && HasSelection)
        {
            ClearSelection();
            e.Handled = true;
        }
    }

    private void SelectAll()
    {
        if (_selection.Text.IsEmpty) return;
        _selection.SelectAll();
        _report($"Selected all {_selection.SelectedText.Length} characters. Press Ctrl+C to copy.");
    }

    private void CopySelection()
    {
        var text = _selection.SelectedText;
        if (text.Length == 0) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        _ = CopyAsync(clipboard, text);
    }

    private async Task CopyAsync(Avalonia.Input.Platform.IClipboard clipboard, string text)
    {
        try
        {
            await clipboard.SetTextAsync(text);
            _report($"Copied {text.Length} characters to the clipboard.");
        }
        catch (Exception)
        {
            _report("Could not copy the selected text.");
        }
    }

    public void ClearSelection() => _selection.Clear();

    public void SetScale(double scale)
    {
        _scale = Math.Max(0.01, scale);
        Width = CssSize.Width * _scale;
        Height = CssSize.Height * _scale;
        _image.Width = Width;
        _image.Height = Height;
        _layer.Width = Width;
        _layer.Height = Height;
        foreach (var (button, area) in _links)
        {
            SetLeft(button, area.X * _scale);
            SetTop(button, area.Y * _scale);
            button.Width = area.Width * _scale;
            button.Height = area.Height * _scale;
        }
        _layer.InvalidateVisual();
    }

    /// <summary>Paints this tile's part of the shared selection; one control drawing rectangles, however long the selection is.</summary>
    private sealed class HighlightLayer(HtmlPageView owner) : Control
    {
        public override void Render(DrawingContext context)
        {
            var selection = owner._selection;
            if (selection.IsEmpty) return;
            var top = owner._top;
            var bottom = top + owner.CssSize.Height;
            var scale = owner._scale;
            foreach (var rect in selection.Text.Highlights(selection.Start, selection.End, top, bottom))
                context.FillRectangle(SelectionTint,
                    new Avalonia.Rect(rect.X * scale, (rect.Y - top) * scale, rect.Width * scale, rect.Height * scale));
        }
    }
}
