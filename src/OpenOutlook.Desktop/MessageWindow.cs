using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace OpenOutlook.Desktop;

/// <summary>An independent read-only message window; changing the main selection cannot alter it.</summary>
public sealed class MessageWindow : Window
{
    private readonly string _plain;
    private readonly string? _html;
    private readonly Dictionary<string, byte[]> _images;
    private readonly StackPanel _pages = new() { Spacing = 0, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _plainBlock = new() { TextWrapping = TextWrapping.Wrap, IsReadOnly = true,
        BorderThickness = new Thickness(0), Background = Brushes.Transparent, IsVisible = false };
    private readonly TextBlock _status = new();
    private readonly Button _toggle = new() { Content = "View plain text" };
    private readonly List<Bitmap> _bitmaps = [];
    private CancellationTokenSource? _renderCancellation;
    private Button? _originalButton;
    private bool _showHtml;
    private bool _showOriginal;
    private double _zoom = 1;

    public MessageWindow(string subject, string sender, string recipient, string plain,
        string? html, IReadOnlyDictionary<string, byte[]> images, bool originalMode = false,
        Action<string>? respond = null)
    {
        Title = string.IsNullOrWhiteSpace(subject) ? "Message — OpenOutlook" : subject + " — OpenOutlook";
        Width = 1100;
        Height = 850;
        MinWidth = 550;
        MinHeight = 350;
        _plain = plain;
        _html = html;
        _images = new Dictionary<string, byte[]>(images, StringComparer.Ordinal);
        _showHtml = !string.IsNullOrWhiteSpace(html);
        _showOriginal = originalMode && _showHtml;
        _plainBlock.Text = plain;
        _plainBlock.IsVisible = !_showHtml;
        _pages.IsVisible = _showHtml;

        var heading = new TextBlock { Text = subject, FontSize = 20, FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap };
        var senderLine = new TextBlock { Text = sender, TextWrapping = TextWrapping.Wrap };
        var recipientLine = new TextBlock { Text = recipient, TextWrapping = TextWrapping.Wrap };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (respond is not null)
        {
            foreach (var (label, action) in new[]
                { ("Reply", "reply"), ("Reply All", "replyAll"), ("Forward", "forward") })
            {
                var button = new Button { Content = label };
                button.Click += (_, _) => respond(action);
                buttons.Children.Add(button);
            }
        }
        _toggle.IsVisible = _showHtml;
        _toggle.Click += (_, _) => ToggleView();
        buttons.Children.Add(_toggle);
        var zoomOut = new Button { Content = "−" };
        var zoomIn = new Button { Content = "+" };
        var zoomFit = new Button { Content = "Fit" };
        zoomOut.Click += (_, _) => { _zoom = Math.Max(0.5, Math.Round(_zoom / 1.25, 2)); FitPages(); };
        zoomIn.Click += (_, _) => { _zoom = Math.Min(2.5, Math.Round(_zoom * 1.25, 2)); FitPages(); };
        zoomFit.Click += (_, _) => { _zoom = 1; FitPages(); };
        buttons.Children.Add(zoomOut);
        buttons.Children.Add(zoomIn);
        buttons.Children.Add(zoomFit);
        if (_showHtml)
        {
            _originalButton = new Button { Content = _showOriginal ? "View safe layout" : "View original here (trusted mail)" };
            _originalButton.Click += (_, _) =>
            {
                _showOriginal = !_showOriginal;
                _showHtml = true;
                _pages.IsVisible = true;
                _plainBlock.IsVisible = false;
                _toggle.Content = "View plain text";
                _ = RenderAsync(_showOriginal);
                _originalButton.Content = _showOriginal ? "View safe layout" : "View original here (trusted mail)";
            };
            buttons.Children.Add(_originalButton);
        }
        var header = new StackPanel { Spacing = 7, Margin = new Thickness(12) };
        header.Children.Add(heading);
        header.Children.Add(senderLine);
        header.Children.Add(recipientLine);
        header.Children.Add(buttons);
        header.Children.Add(_status);

        var body = new Grid();
        body.Children.Add(_pages);
        body.Children.Add(_plainBlock);
        var scroll = new ScrollViewer { Content = body, Margin = new Thickness(12, 0, 12, 12) };
        var layout = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        layout.Children.Add(scroll);
        Content = layout;
        SizeChanged += (_, _) => FitPages();
        Opened += (_, _) => { if (_showHtml) _ = RenderAsync(_showOriginal); };
        Closed += (_, _) =>
        {
            var cancellation = _renderCancellation;
            _renderCancellation = null;
            cancellation?.Cancel();
            _pages.Children.Clear();
            foreach (var bitmap in _bitmaps) bitmap.Dispose();
            _bitmaps.Clear();
        };
    }

    public void UpdateImages(IReadOnlyDictionary<string, byte[]> images)
    {
        if (!IsVisible || !_showHtml || _showOriginal) return;
        _images.Clear();
        foreach (var image in images) _images[image.Key] = image.Value;
        _ = RenderAsync(false);
    }

    private async Task RenderAsync(bool trustedOriginal)
    {
        var previousCancellation = _renderCancellation;
        _renderCancellation = null;
        previousCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _renderCancellation = cancellation;
        try
        {
            _status.Text = trustedOriginal ? "Loading original message and external content…" : "Laying out message…";
            var document = trustedOriginal ? _html! : SafeHtmlDocument.Build(_html!, _images);
            var width = (int)Math.Clamp(Bounds.Width - 40, 400, 1200);
            var rendered = await BrowserHtmlRenderer.RenderDocumentAsync(document, width, cancellation.Token,
                trustedOriginal: trustedOriginal);
            if (cancellation.IsCancellationRequested) return;
            var bitmaps = rendered.Pages.Select(png => new Bitmap(new MemoryStream(png, writable: false))).ToArray();
            _pages.Children.Clear();
            foreach (var bitmap in _bitmaps) bitmap.Dispose();
            _bitmaps.Clear();
            double top = 0;
            foreach (var bitmap in bitmaps)
            {
                _bitmaps.Add(bitmap);
                _pages.Children.Add(new HtmlPageView(bitmap, top, rendered.Links,
                    message => _status.Text = message));
                top += bitmap.PixelSize.Height;
            }
            FitPages();
            _status.Text = "";
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (cancellation.IsCancellationRequested) return;
            _status.Text = trustedOriginal
                ? "Could not load the original message; the current view is still available."
                : "Full HTML layout is unavailable; showing plain text.";
            if (!trustedOriginal && _bitmaps.Count == 0)
            {
                _showHtml = false;
                _pages.IsVisible = false;
                _plainBlock.IsVisible = true;
            }
        }
        finally
        {
            if (ReferenceEquals(_renderCancellation, cancellation))
                _renderCancellation = null;
            cancellation.Dispose();
        }
    }

    private void FitPages()
    {
        if (_bitmaps.Count == 0) return;
        var available = Math.Max(200, Bounds.Width - 40);
        for (var i = 0; i < _bitmaps.Count; i++)
        {
            var bitmap = _bitmaps[i];
            var scale = Math.Min(1, available / bitmap.PixelSize.Width) * _zoom;
            ((HtmlPageView)_pages.Children[i]).SetScale(scale);
        }
    }

    private void ToggleView()
    {
        _showHtml = !_showHtml;
        _pages.IsVisible = _showHtml;
        _plainBlock.IsVisible = !_showHtml;
        _toggle.Content = _showHtml ? "View plain text" : "View rich text";
    }

}
