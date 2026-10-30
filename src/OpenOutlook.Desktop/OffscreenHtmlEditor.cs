using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PuppeteerSharp;

namespace OpenOutlook.Desktop;

/// <summary>
/// The editor page (<see cref="HtmlEditorDocument.Shell"/>) in a headless Chromium, for desktops where the embedded web view paints nothing
/// (Wayland). The page renders off screen; its frames come back as pictures (DevTools screencast) and the input of the compose body goes to it as
/// DevTools input events. All calls go through one queue, so keys, text and ribbon commands reach the page in the order they were made.
/// </summary>
internal sealed class OffscreenEditorSession : IAsyncDisposable
{
    private readonly IBrowser _browser;
    private readonly IPage _page;
    private readonly SemaphoreSlim _queue = new(1, 1);
    private bool _closed;

    private OffscreenEditorSession(IBrowser browser, IPage page) { _browser = browser; _page = page; }

    /// <summary>What the page posts ("dirty", "html:...", "state:...").</summary>
    public event Action<string>? Message;
    /// <summary>A new picture of the page: JPEG bytes and the size in CSS pixels it shows.</summary>
    public event Action<byte[], double, double>? Frame;

    public static async Task<OffscreenEditorSession> StartAsync(string executable, int width, int height, double scale,
        Action<string> message, Action<byte[], double, double> frame)
    {
        var browser = await BrowserProcessTracker.LaunchAsync(executable).ConfigureAwait(false);
        try
        {
            var page = (await browser.PagesAsync().ConfigureAwait(false)).FirstOrDefault() ?? await browser.NewPageAsync().ConfigureAwait(false);
            var session = new OffscreenEditorSession(browser, page);
            session.Message += message;
            session.Frame += frame;
            await page.SetViewportAsync(Viewport(width, height, scale)).ConfigureAwait(false);
            await page.Client.SendAsync("Emulation.setFocusEmulationEnabled", new Dictionary<string, object> { ["enabled"] = true }).ConfigureAwait(false);
            await page.ExposeFunctionAsync("ooPost", (string m) => { session.Message?.Invoke(m); return true; }).ConfigureAwait(false);
            page.Client.MessageReceived += session.OnDevToolsMessage;
            await page.SetContentAsync(HtmlEditorDocument.Shell).ConfigureAwait(false);
            await page.Client.SendAsync("Page.startScreencast", new Dictionary<string, object> { ["format"] = "jpeg", ["quality"] = 92 }).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await browser.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static ViewPortOptions Viewport(int width, int height, double scale) =>
        new() { Width = Math.Clamp(width, 50, 8000), Height = Math.Clamp(height, 50, 8000), DeviceScaleFactor = Math.Clamp(scale, 1, 3) };

    private async void OnDevToolsMessage(object? sender, MessageEventArgs e)
    {
        if (e.MessageID != "Page.screencastFrame" || _closed) return;
        try
        {
            var data = e.MessageData;
            var meta = data.GetProperty("metadata");
            var bytes = Convert.FromBase64String(data.GetProperty("data").GetString() ?? "");
            Frame?.Invoke(bytes, meta.GetProperty("deviceWidth").GetDouble(), meta.GetProperty("deviceHeight").GetDouble());
            await _page.Client.SendAsync("Page.screencastFrameAck", new Dictionary<string, object> { ["sessionId"] = data.GetProperty("sessionId").GetInt32() }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (!_closed) AppLog.Note("compose", "editor frame failed: " + ex.Message); }
    }

    private async Task<T?> Queued<T>(Func<Task<T>> call)
    {
        if (_closed) return default;
        await _queue.WaitAsync().ConfigureAwait(false);
        try { return _closed ? default : await call().ConfigureAwait(false); }
        catch (Exception e) when (e is not OutOfMemoryException) { if (!_closed) AppLog.Note("compose", "editor call failed: " + e.Message); return default; }
        finally { _queue.Release(); }
    }

    private Task Send(string method, Dictionary<string, object> args) => Queued(async () => { await _page.Client.SendAsync(method, args).ConfigureAwait(false); return true; });

    /// <summary>Runs a script in the page; the result comes back as JSON text (what <see cref="HtmlEditorDocument.ScriptString"/> reads), null on failure.</summary>
    public Task<string?> EvaluateAsync(string script) => Queued(() => _page.EvaluateExpressionAsync<string?>("JSON.stringify((" + script + "))"));

    public Task ResizeAsync(int width, int height, double scale) => Queued(async () => { await _page.SetViewportAsync(Viewport(width, height, scale)).ConfigureAwait(false); return true; });

    public Task InsertTextAsync(string text) => Send("Input.insertText", new() { ["text"] = text });

    /// <summary>A key that is not text (arrows, Backspace, Enter, Ctrl+B, ...): Chromium's own editing behaviour handles it, as in a browser.</summary>
    public Task KeyAsync(OffscreenKey key, int modifiers) => Queued(async () =>
    {
        var down = new Dictionary<string, object>
        {
            ["type"] = key.Text is null ? "rawKeyDown" : "keyDown", ["key"] = key.Name, ["code"] = key.Code,
            ["windowsVirtualKeyCode"] = key.VirtualKey, ["modifiers"] = modifiers,
        };
        if (key.Text is not null) { down["text"] = key.Text; down["unmodifiedText"] = key.Text; }
        await _page.Client.SendAsync("Input.dispatchKeyEvent", down).ConfigureAwait(false);
        await _page.Client.SendAsync("Input.dispatchKeyEvent", new Dictionary<string, object>
        {
            ["type"] = "keyUp", ["key"] = key.Name, ["code"] = key.Code, ["windowsVirtualKeyCode"] = key.VirtualKey, ["modifiers"] = modifiers,
        }).ConfigureAwait(false);
        return true;
    });

    /// <summary>A mouse event in CSS pixels of the page (type mousePressed, mouseReleased, mouseMoved or mouseWheel).</summary>
    public Task MouseAsync(string type, double x, double y, string button, int buttons, int clickCount, int modifiers, double deltaX = 0, double deltaY = 0)
    {
        var args = new Dictionary<string, object>
        {
            ["type"] = type, ["x"] = x, ["y"] = y, ["button"] = button, ["buttons"] = buttons, ["clickCount"] = clickCount, ["modifiers"] = modifiers,
        };
        if (type == "mouseWheel") { args["deltaX"] = deltaX; args["deltaY"] = deltaY; }
        return Send("Input.dispatchMouseEvent", args);
    }

    public async ValueTask DisposeAsync()
    {
        if (_closed) return;
        _closed = true;
        _page.Client.MessageReceived -= OnDevToolsMessage;
        try { await _browser.DisposeAsync().ConfigureAwait(false); }
        catch (Exception e) when (e is not OutOfMemoryException) { AppLog.Note("compose", "editor browser close failed: " + e.Message); }
    }
}

/// <summary>A key as DevTools takes it; <see cref="Text"/> is what it types (Enter), null for keys that only move or edit.</summary>
internal sealed record OffscreenKey(string Name, string Code, int VirtualKey, string? Text = null)
{
    /// <summary>DevTools modifier bits: Alt 1, Ctrl 2, Meta 4, Shift 8.</summary>
    public static int Modifiers(KeyModifiers m) =>
        (m.HasFlag(KeyModifiers.Alt) ? 1 : 0) | (m.HasFlag(KeyModifiers.Control) ? 2 : 0) | (m.HasFlag(KeyModifiers.Meta) ? 4 : 0) | (m.HasFlag(KeyModifiers.Shift) ? 8 : 0);

    /// <summary>
    /// The keys the body passes on as key events. Text arrives separately (TextInput); Ctrl+letter shortcuts (bold, undo, select all, ...) are
    /// Chromium's; Tab keeps moving the focus and Ctrl+C / X / V go through the desktop clipboard, so those return null.
    /// </summary>
    public static OffscreenKey? From(Key key, KeyModifiers modifiers)
    {
        var ctrl = modifiers.HasFlag(KeyModifiers.Control);
        switch (key)
        {
            case Key.Back: return new("Backspace", "Backspace", 8);
            case Key.Delete: return new("Delete", "Delete", 46);
            case Key.Enter: return new("Enter", "Enter", 13, "\r");
            case Key.Left: return new("ArrowLeft", "ArrowLeft", 37);
            case Key.Up: return new("ArrowUp", "ArrowUp", 38);
            case Key.Right: return new("ArrowRight", "ArrowRight", 39);
            case Key.Down: return new("ArrowDown", "ArrowDown", 40);
            case Key.Home: return new("Home", "Home", 36);
            case Key.End: return new("End", "End", 35);
            case Key.PageUp: return new("PageUp", "PageUp", 33);
            case Key.PageDown: return new("PageDown", "PageDown", 34);
        }
        if (ctrl && key is >= Key.A and <= Key.Z && key is not (Key.C or Key.X or Key.V))
        {
            var letter = (char)('a' + (key - Key.A));
            var shown = modifiers.HasFlag(KeyModifiers.Shift) ? char.ToUpperInvariant(letter) : letter;
            return new(shown.ToString(), "Key" + char.ToUpperInvariant(letter), 'A' + (key - Key.A));
        }
        return null;
    }
}

/// <summary>
/// The compose body on Wayland: <see cref="OffscreenEditorSession"/> drawn into the window and typed into in place, with the same page and commands
/// as the embedded editor on Windows. If no browser starts, the body falls back to <see cref="PreviewHtmlEditor"/>.
/// </summary>
internal sealed class OffscreenHtmlEditor : IHtmlEditorHost
{
    private readonly Panel _panel = new() { Background = Brushes.White };
    private readonly OffscreenEditorView _view;
    private readonly TextBlock _starting = new() { Text = "Starting the editor…", Foreground = Brushes.Gray, Margin = new Thickness(14, 10) };
    private readonly Func<TopLevel?> _owner;
    private readonly Action<string> _status;
    private readonly List<string> _queued = [];
    private OffscreenEditorSession? _session;
    private PreviewHtmlEditor? _fallback;
    private bool _started, _ready;
    private string? _pendingHtml;
    private string _lastHtml = "";

    public OffscreenHtmlEditor(Func<TopLevel?> owner, Action<string> status)
    {
        _owner = owner;
        _status = status;
        _view = new OffscreenEditorView(this);
        _panel.Children.Add(_view);
        _panel.Children.Add(_starting);
        _panel.AttachedToVisualTree += (_, _) => { if (!_started) { _started = true; _ = StartAsync(); } };
        _panel.DetachedFromVisualTree += (_, _) => { if (_session is { } s) { _session = null; _ready = false; _ = s.DisposeAsync().AsTask(); } };
    }

    public Control View => _panel;
    public bool EditsInPlace => _fallback is null;
    internal bool IsReady => _ready && _pendingHtml is null;
    internal OffscreenEditorSession? Session => _session;
    public event Action<string>? HtmlChanged;
    public event Action? Edited;
    public event Action<HtmlEditorState>? StateChanged;

    private async Task StartAsync()
    {
        try
        {
            var exe = BrowserHtmlRenderer.FindBrowser() ?? throw new InvalidOperationException("no Chromium-based browser found");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);           // the body has its size
            var (w, h, scale) = _view.ViewportSize();
            var session = await OffscreenEditorSession.StartAsync(exe, w, h, scale, m => Dispatcher.UIThread.Post(() => OnMessage(m)), _view.OnFrame);
            if (TopLevel.GetTopLevel(_panel) is null) { await session.DisposeAsync(); return; }           // the window closed while the browser started
            _session = session;
            if (_pendingHtml is { } html) { _pendingHtml = null; await session.EvaluateAsync(HtmlEditorDocument.SetCall(html)); }
            foreach (var script in _queued.ToList()) await session.EvaluateAsync(script);
            _queued.Clear();
            _ready = true;
            _starting.IsVisible = false;
            _view.Resized();
            if (_view.IsFocused) _ = session.EvaluateAsync("window.oo&&window.oo.focus()");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            AppLog.Note("compose", "off-screen editor did not start, using the preview: " + e.Message);
            UseFallback();
        }
    }

    private void UseFallback()
    {
        var html = _pendingHtml ?? _lastHtml;
        _fallback = new PreviewHtmlEditor(_owner, _status);
        _fallback.HtmlChanged += h => HtmlChanged?.Invoke(h);
        _fallback.Edited += () => Edited?.Invoke();
        _fallback.Load(html);
        _panel.Children.Clear();
        _panel.Children.Add(_fallback.View);
    }

    private void OnMessage(string body)
    {
        if (body == "dirty") Edited?.Invoke();
        else if (body.StartsWith("html:", StringComparison.Ordinal)) { _lastHtml = body[5..]; HtmlChanged?.Invoke(_lastHtml); }
        else if (body.StartsWith("state:", StringComparison.Ordinal) && HtmlEditorDocument.ParseState(body[6..]) is { } state) StateChanged?.Invoke(state);
    }

    public void Load(string html)
    {
        _lastHtml = html;
        if (_fallback is not null) _fallback.Load(html);
        else if (_ready && _session is { } s) _ = s.EvaluateAsync(HtmlEditorDocument.SetCall(html));
        else _pendingHtml = html;
    }

    public void Run(string command, string? value = null)
    {
        if (_fallback is not null) { _fallback.Run(command, value); return; }
        var script = HtmlEditorDocument.Call(command, value);
        if (_ready && _session is { } s) _ = s.EvaluateAsync(script);
        else _queued.Add(script);
    }

    public async Task<string?> ReadHtmlAsync()
    {
        if (_fallback is not null) return await _fallback.ReadHtmlAsync();
        return _ready && _session is { } s ? HtmlEditorDocument.ScriptString(await s.EvaluateAsync("window.oo&&window.oo.get()")) ?? _lastHtml : _pendingHtml ?? _lastHtml;
    }

    public async Task<bool> FindAsync(string text)
    {
        if (_fallback is not null) return await _fallback.FindAsync(text);
        return _ready && _session is { } s &&
               string.Equals(HtmlEditorDocument.ScriptString(await s.EvaluateAsync("window.oo&&window.oo.find(" + JsonSerializer.Serialize(text) + ")")), "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string?> SelectedTextAsync()
    {
        if (_fallback is not null) return await _fallback.SelectedTextAsync();
        return _ready && _session is { } s ? HtmlEditorDocument.ScriptString(await s.EvaluateAsync("window.oo&&window.oo.selText()")) : null;
    }

    public void FocusBody()
    {
        if (_fallback is not null) { _fallback.FocusBody(); return; }
        _view.Focus();
        if (_ready && _session is { } s) _ = s.EvaluateAsync("window.oo&&window.oo.focus()");
    }

    /// <summary>Ctrl+C / Ctrl+X: the selection to the desktop clipboard as HTML and text.</summary>
    internal async Task CopyAsync(bool cut)
    {
        if (_session is not { } s || _owner()?.Clipboard is not { } clipboard) return;
        var html = HtmlEditorDocument.ScriptString(await s.EvaluateAsync("window.oo&&window.oo.selHtml()"));
        var text = HtmlEditorDocument.ScriptString(await s.EvaluateAsync("window.oo&&window.oo.selText()"));
        if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(html)) return;
        try
        {
            var data = new DataObject();
            data.Set(DataFormats.Text, text ?? "");
            if (!string.IsNullOrEmpty(html)) data.Set("text/html", html);
            await clipboard.SetDataObjectAsync(data);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { await clipboard.SetTextAsync(text ?? ""); }
        if (cut) Run("delete");
    }

    /// <summary>Ctrl+V: HTML from the desktop clipboard (sanitized, as a reply's quote is), else a picture, else plain text.</summary>
    internal async Task PasteAsync()
    {
        if (_owner()?.Clipboard is not { } clipboard) return;
        try
        {
            var formats = await clipboard.GetFormatsAsync() ?? [];
            if (formats.Contains("text/html") && ClipboardText(await clipboard.GetDataAsync("text/html")) is { Length: > 0 } html)
            {
                var safe = ComposeHtml.Sanitize(html);
                if (!string.IsNullOrWhiteSpace(safe)) { Run("html", safe); return; }
            }
            if (formats.Contains("image/png") && await clipboard.GetDataAsync("image/png") is byte[] { Length: > 0 and < 20 * 1024 * 1024 } png)
            {
                Run("image", "data:image/png;base64," + Convert.ToBase64String(png));
                return;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException) { AppLog.Note("compose", "rich paste failed, pasting text: " + e.Message); }
        var text = await clipboard.GetTextAsync();
        if (!string.IsNullOrEmpty(text)) Run("insertText", text);
    }

    /// <summary>Clipboard data as text: X11 hands HTML over as bytes (UTF-8, or UTF-16 with a byte order mark from some programs).</summary>
    internal static string? ClipboardText(object? data) => data switch
    {
        string s => s,
        byte[] { Length: >= 2 } b when b[0] == 0xFF && b[1] == 0xFE => System.Text.Encoding.Unicode.GetString(b, 2, b.Length - 2),
        byte[] b => System.Text.Encoding.UTF8.GetString(b).TrimEnd('\0'),
        _ => null,
    };
}

/// <summary>Draws the page's latest frame and hands keyboard, mouse and wheel input to the page.</summary>
internal sealed class OffscreenEditorView : Control
{
    private readonly OffscreenHtmlEditor _owner;
    private Bitmap? _frame;
    private Size _frameSize;
    private byte[]? _nextFrame;
    private (double W, double H) _nextSize;
    private bool _decoding;
    private DispatcherTimer? _resizeTimer;
    private (double X, double Y, int Buttons, int Modifiers)? _pendingMove;
    private bool _moveSending;

    public OffscreenEditorView(OffscreenHtmlEditor owner)
    {
        _owner = owner;
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        var menu = new ContextMenu();
        menu.Items.Add(Item("Cut", () => _owner.CopyAsync(true)));
        menu.Items.Add(Item("Copy", () => _owner.CopyAsync(false)));
        menu.Items.Add(Item("Paste", _owner.PasteAsync));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Select All", () => { _owner.Run("selectAll"); return Task.CompletedTask; }));
        ContextMenu = menu;
    }

    private static MenuItem Item(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => await action();
        return item;
    }

    /// <summary>The page's size in CSS pixels (the body's size in Avalonia units) and the screen's scale.</summary>
    internal (int Width, int Height, double Scale) ViewportSize()
    {
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        return ((int)Math.Max(50, Math.Round(Bounds.Width)), (int)Math.Max(50, Math.Round(Bounds.Height)), scale);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _resizeTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Background, (_, _) => { _resizeTimer!.Stop(); Resized(); });
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    internal void Resized()
    {
        if (_owner.Session is not { } s) return;
        var (w, h, scale) = ViewportSize();
        _ = s.ResizeAsync(w, h, scale);
    }

    /// <summary>A frame from the browser (any thread). Only the newest one is decoded; frames that arrive meanwhile replace each other.</summary>
    internal void OnFrame(byte[] jpeg, double cssWidth, double cssHeight)
    {
        lock (this)
        {
            _nextFrame = jpeg;
            _nextSize = (cssWidth, cssHeight);
            if (_decoding) return;
            _decoding = true;
        }
        _ = Task.Run(DecodeLoop);
    }

    private void DecodeLoop()
    {
        while (true)
        {
            byte[] bytes;
            (double W, double H) size;
            lock (this)
            {
                if (_nextFrame is null) { _decoding = false; return; }
                bytes = _nextFrame;
                size = _nextSize;
                _nextFrame = null;
            }
            Bitmap bitmap;
            try { bitmap = new Bitmap(new MemoryStream(bytes)); }
            catch (Exception e) when (e is not OutOfMemoryException) { AppLog.Note("compose", "editor frame decode failed: " + e.Message); continue; }
            Dispatcher.UIThread.Post(() =>
            {
                var old = _frame;
                _frame = bitmap;
                _frameSize = new Size(size.W, size.H);
                InvalidateVisual();
                if (old is not null) Dispatcher.UIThread.Post(old.Dispose, DispatcherPriority.Background);
            });
        }
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.White, new Rect(Bounds.Size));
        if (_frame is { } frame)
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                context.DrawImage(frame, new Rect(frame.Size), new Rect(_frameSize));
    }

    // ------------------------------------------------------------------ input

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        _ = _owner.Session?.EvaluateAsync("window.oo&&window.oo.focus()");
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        if (ContextMenu?.IsOpen == true) return;                                             // the selection stays for Cut / Copy
        _ = _owner.Session?.EvaluateAsync("document.activeElement&&document.activeElement.blur()");
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (string.IsNullOrEmpty(e.Text) || _owner.Session is not { } s) return;
        var text = e.Text.Replace("\r", "").Replace("\n", "");
        if (text.Length == 0 || char.IsControl(text[0])) return;
        _ = s.InsertTextAsync(text);
        e.Handled = true;
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_owner.Session is not { } s) return;
        var m = e.KeyModifiers;
        var ctrl = m.HasFlag(KeyModifiers.Control);
        var shift = m.HasFlag(KeyModifiers.Shift);
        if ((ctrl && e.Key is Key.C or Key.Insert && !shift) || (ctrl && e.Key == Key.X) || (shift && e.Key == Key.Delete))
        {
            e.Handled = true;
            await _owner.CopyAsync(cut: e.Key is Key.X or Key.Delete);
            return;
        }
        if ((ctrl && e.Key == Key.V) || (shift && e.Key == Key.Insert))
        {
            e.Handled = true;
            await _owner.PasteAsync();
            return;
        }
        if (OffscreenKey.From(e.Key, m) is not { } key) return;
        e.Handled = true;
        await s.KeyAsync(key, OffscreenKey.Modifiers(m));
    }

    private static string Button(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => "left",
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => "right",
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => "middle",
        _ => "none",
    };

    private static int Buttons(PointerPointProperties p) => (p.IsLeftButtonPressed ? 1 : 0) | (p.IsRightButtonPressed ? 2 : 0) | (p.IsMiddleButtonPressed ? 4 : 0);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (_owner.Session is not { } s) return;
        var point = e.GetCurrentPoint(this);
        var button = Button(point.Properties.PointerUpdateKind);
        if (button == "right") return;                                                       // the context menu; the selection stays
        e.Handled = true;
        _ = s.MouseAsync("mousePressed", point.Position.X, point.Position.Y, button, Buttons(point.Properties), e.ClickCount, OffscreenKey.Modifiers(e.KeyModifiers));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_owner.Session is not { } s) return;
        var point = e.GetCurrentPoint(this);
        var button = Button(point.Properties.PointerUpdateKind);
        if (button == "right") return;
        _ = FlushMoveThen(() => s.MouseAsync("mouseReleased", point.Position.X, point.Position.Y, button, Buttons(point.Properties), 1, OffscreenKey.Modifiers(e.KeyModifiers)));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_owner.Session is null) return;
        var point = e.GetCurrentPoint(this);
        _pendingMove = (point.Position.X, point.Position.Y, Buttons(point.Properties), OffscreenKey.Modifiers(e.KeyModifiers));
        if (!_moveSending) _ = SendMoves();
    }

    /// <summary>Mouse moves are merged while one is on its way, so a drag never queues up behind itself.</summary>
    private async Task SendMoves()
    {
        _moveSending = true;
        try
        {
            while (_pendingMove is { } move && _owner.Session is { } s)
            {
                _pendingMove = null;
                await s.MouseAsync("mouseMoved", move.X, move.Y, (move.Buttons & 1) != 0 ? "left" : "none", move.Buttons, 0, move.Modifiers);
            }
        }
        finally { _moveSending = false; }
    }

    private async Task FlushMoveThen(Func<Task> next)
    {
        if (_pendingMove is not null && !_moveSending) await SendMoves();
        await next();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_owner.Session is not { } s) return;
        e.Handled = true;
        var p = e.GetPosition(this);
        _ = s.MouseAsync("mouseWheel", p.X, p.Y, "none", 0, 0, OffscreenKey.Modifiers(e.KeyModifiers), -e.Delta.X * 60, -e.Delta.Y * 60);
    }
}
