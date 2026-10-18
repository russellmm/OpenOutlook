using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Reactive;

namespace OpenOutlook.Desktop;

/// <summary>
/// HTML editing in the message body (owner 2026-10-18, Outlook's Format Text tab): the body is an editable page in the embedded web view and the
/// Message tab's Basic Text and the Format Text tab drive it. A message is HTML or plain text (no Rich Text); switching to plain text warns that the
/// formatting goes. Formatting controls are greyed in a plain-text message, as in Outlook.
/// </summary>
public sealed partial class ComposeWindow
{
    private static readonly IBrush Pressed = new SolidColorBrush(Color.Parse("#CCE4F7"));
    private static List<string>? _fontNames;

    /// <summary>Tests replace the editor (the embedded web view cannot start in the headless test host).</summary>
    internal static Func<ComposeWindow, IHtmlEditorHost>? EditorFactory { get; set; }

    internal IHtmlEditorHost? Editor => _editor;
    internal bool IsHtml => _isHtml;

    private static readonly (string Name, string Hex)[] HighlightColors =
    [
        ("Yellow", "#FFFF00"), ("Bright Green", "#00FF00"), ("Turquoise", "#00FFFF"), ("Pink", "#FF00FF"), ("Blue", "#0000FF"),
        ("Red", "#FF0000"), ("Dark Blue", "#000080"), ("Teal", "#008080"), ("Green", "#008000"), ("Violet", "#800080"),
        ("Dark Red", "#800000"), ("Dark Yellow", "#808000"), ("Gray 50%", "#808080"), ("Gray 25%", "#C0C0C0"), ("Black", "#000000"),
    ];

    private static readonly (string Name, string Hex)[] ThemeColors =
    [
        ("Black", "#000000"), ("White", "#FFFFFF"), ("Light Gray", "#E7E6E6"), ("Blue-Gray", "#44546A"), ("Blue", "#4472C4"),
        ("Orange", "#ED7D31"), ("Gray", "#A5A5A5"), ("Gold", "#FFC000"), ("Light Blue", "#5B9BD5"), ("Green", "#70AD47"),
        ("Dark Red", "#C00000"), ("Red", "#FF0000"), ("Orange", "#FFC000"), ("Yellow", "#FFFF00"), ("Light Green", "#92D050"),
        ("Green", "#00B050"), ("Light Blue", "#00B0F0"), ("Blue", "#0070C0"), ("Dark Blue", "#002060"), ("Purple", "#7030A0"),
    ];

    // ---------------------------------------------------------------- the body

    /// <summary>The editor is made the first time the message is HTML.</summary>
    private IHtmlEditorHost EnsureEditor()
    {
        if (_editor is not null) return _editor;
        var headless = PlatformImpl?.GetType().Assembly.GetName().Name?.Contains("Headless", StringComparison.OrdinalIgnoreCase) == true;
        _editor = EditorFactory?.Invoke(this)
                  ?? (!headless && WebHtmlEditor.Supported() ? new WebHtmlEditor() : new PreviewHtmlEditor(() => this, text => _status.Text = text));
        _editor.Edited += () => { if (!_loadingDraft) { _dirty = true; _bodyEdited = true; } };
        _editor.HtmlChanged += html => { _htmlBody = html; _bodyEdited = true; if (!_loadingDraft) _dirty = true; };
        _editor.StateChanged += ApplyEditorState;
        _bodyHost.Children.Add(_editor.View);
        if (Math.Abs(_zoom - 1) > 0.001) _editor.Run("zoom", _zoom.ToString(CultureInfo.InvariantCulture));
        if (_editor is WebHtmlEditor && OperatingSystem.IsLinux()) GuardEditorUnderPopups();
        return _editor;
    }

    private bool _bodyEdited;

    /// <summary>Puts HTML in the body (seed, draft); what is shown is sanitized, untrusted mail can be in a reply.</summary>
    private void ShowHtmlBody(string html)
    {
        _isHtml = true;
        _htmlBody = html;
        _bodyEdited = false;
        EnsureEditor().Load(ComposeHtml.Sanitize(html));
    }

    /// <summary>The editor's latest HTML into the draft before it is saved or sent (the page reports changes a moment late).</summary>
    private async Task FlushEditorAsync()
    {
        if (!_isHtml || _editor is null || !_bodyEdited) return;
        var raw = await _editor.ReadHtmlAsync();
        if (raw is not null) _htmlBody = ComposeHtml.Sanitize(raw);
    }

    /// <summary>On Linux pop-ups are drawn inside the window, under the native web view: the view steps aside while one of this window's pop-ups is open.</summary>
    private void GuardEditorUnderPopups()
    {
        var open = new HashSet<Popup>();
        var subscription = Popup.IsOpenProperty.Changed.Subscribe(new AnonymousObserver<AvaloniaPropertyChangedEventArgs<bool>>(e =>
        {
            if (e.Sender is not Popup popup || TopLevel.GetTopLevel(popup.PlacementTarget ?? popup) != this) return;
            if (e.NewValue.GetValueOrDefault()) open.Add(popup); else open.Remove(popup);
            if (_editor is not null) _editor.View.IsVisible = _isHtml && open.Count == 0;
        }));
        Closed += (_, _) => subscription.Dispose();
    }

    private async Task SetFormatAsync(bool html)
    {
        if (html == _isHtml || _busy) return;
        if (html)
        {
            ShowHtmlBody(HtmlEditorDocument.FromPlainText(_body.Text ?? ""));
            _bodyEdited = true;
        }
        else
        {
            await FlushEditorAsync();
            if (!HtmlEditorDocument.IsBlank(_htmlBody) &&
                !await ConfirmAsync("Change to plain text", "Plain text cannot show the formatting of this message: fonts, colours, lists, pictures and tables will be lost. Continue?"))
            { UpdateBodyMode(); return; }
            _body.Text = PreviewText(_htmlBody);
            _isHtml = false;
        }
        _dirty = true;
        UpdateBodyMode();
        UpdateTitle();
        if (_isHtml) _editor?.FocusBody(); else _body.Focus();
    }

    private void UpdateBodyMode()
    {
        _body.IsVisible = !_isHtml;
        if (_editor is not null) _editor.View.IsVisible = _isHtml;
        foreach (var c in _htmlOnly) c.IsEnabled = _isHtml && !_busy && !_loadFailed;
        foreach (var (button, html) in _formatButtons)
        {
            button.Background = html == _isHtml ? Pressed : Brushes.Transparent;
            button.IsEnabled = !_busy && !_loadFailed;
        }
    }

    private void ApplyEditorState(HtmlEditorState state)
    {
        _syncingState = true;
        try
        {
            var on = new Dictionary<string, bool>
            {
                ["bold"] = state.Bold, ["italic"] = state.Italic, ["underline"] = state.Underline, ["strikeThrough"] = state.Strike,
                ["subscript"] = state.Subscript, ["superscript"] = state.Superscript, ["bullets"] = state.Bullets, ["numbering"] = state.Numbering,
                ["justifyLeft"] = state.Align == "left", ["justifyCenter"] = state.Align == "center", ["justifyRight"] = state.Align == "right",
                ["justifyFull"] = state.Align == "justify", ["marks"] = state.Marks,
            };
            foreach (var (command, buttons) in _toggles)
                foreach (var b in buttons) b.Background = on.TryGetValue(command, out var v) && v ? Pressed : Brushes.Transparent;
            foreach (var box in _fontBoxes)
            {
                var match = FontNames().FirstOrDefault(f => string.Equals(f, state.Font, StringComparison.OrdinalIgnoreCase));
                box.SelectedItem = match;
                box.PlaceholderText = match is null ? state.Font : null;
            }
            var size = state.Size.ToString("0.#", CultureInfo.InvariantCulture);
            foreach (var box in _sizeBoxes)
            {
                var match = box.Items.OfType<string>().FirstOrDefault(i => i == size);
                box.SelectedItem = match;
                box.PlaceholderText = match is null ? size : null;
            }
        }
        finally { _syncingState = false; }
    }

    /// <summary>One formatting command for the body.</summary>
    private void Exec(string command, string? value = null)
    {
        if (!_isHtml) return;
        var editor = EnsureEditor();
        editor.Run(command, value);
        if (editor.EditsInPlace) editor.FocusBody();
        else _status.Text = "On this desktop the message is formatted in its own window: press Edit message… above the body.";
    }

    // ---------------------------------------------------------------- ribbon pieces

    private Control HtmlOnly(Control c) { _htmlOnly.Add(c); return c; }

    private static List<string> FontNames() => _fontNames ??= LoadFontNames();

    private static List<string> LoadFontNames()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try { foreach (var f in FontManager.Current.SystemFonts) names.Add(f.Name); }
        catch (Exception e) when (e is not OutOfMemoryException) { AppLog.Note("compose", "font list not read: " + e.Message); }
        foreach (var common in new[] { "Aptos", "Arial", "Calibri", "Cambria", "Courier New", "Georgia", "Tahoma", "Times New Roman", "Verdana" }) names.Add(common);
        return names.ToList();
    }

    private Control FontBox()
    {
        var box = new ComboBox { Width = 132, Height = 24, MinHeight = 24, Padding = new Thickness(6, 0), FontSize = 12, Margin = new Thickness(0, 0, 3, 0),
            VerticalAlignment = VerticalAlignment.Center, ItemsSource = FontNames(), PlaceholderText = HtmlEditorDocument.DefaultFont, MaxDropDownHeight = 420 };
        ToolTip.SetTip(box, "Font");
        box.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((name, _) => new TextBlock { Text = name, FontFamily = new FontFamily(name ?? "Arial") });
        box.SelectionChanged += (_, _) => { if (!_syncingState && box.SelectedItem is string f) Exec("fontName", f); };
        _fontBoxes.Add(box);
        return HtmlOnly(box);
    }

    private Control SizeBox()
    {
        var box = new ComboBox { Width = 56, Height = 24, MinHeight = 24, Padding = new Thickness(6, 0), FontSize = 12, Margin = new Thickness(0, 0, 3, 0),
            VerticalAlignment = VerticalAlignment.Center, PlaceholderText = HtmlEditorDocument.DefaultSizePt.ToString(CultureInfo.InvariantCulture), MaxDropDownHeight = 420,
            ItemsSource = HtmlEditorDocument.Sizes.Select(v => v.ToString("0.#", CultureInfo.InvariantCulture)).ToList() };
        ToolTip.SetTip(box, "Font Size");
        box.SelectionChanged += (_, _) => { if (!_syncingState && box.SelectedItem is string v) Exec("fontSize", v); };
        _sizeBoxes.Add(box);
        return HtmlOnly(box);
    }

    private Control Cmd(string icon, string caption, string command, string? value = null) =>
        HtmlOnly(Mini(icon, caption, () => { Exec(command, value); return Task.CompletedTask; }));

    private Control Toggle(string? icon, string caption, string command, string? glyph = null)
    {
        var b = glyph is null
            ? Mini(icon, caption, () => { Exec(command); return Task.CompletedTask; })
            : Mini(null, caption, () => { Exec(command); return Task.CompletedTask; }, glyph, glyph == "B" ? FontWeight.Bold : FontWeight.Normal, glyph == "I" ? FontStyle.Italic : FontStyle.Normal);
        if (!_toggles.TryGetValue(command, out var list)) _toggles[command] = list = [];
        list.Add(b);
        return HtmlOnly(b);
    }

    /// <summary>Outlook's split button: the left part repeats the last choice, the arrow opens the choices.</summary>
    private Control Split(string icon, string caption, Action main, Func<Flyout, Control> choices)
    {
        var face = Mini(icon, caption, () => { main(); return Task.CompletedTask; });
        var arrow = new Button { Content = Chevron(7), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(1, 3), MinHeight = 24, Focusable = false };
        ToolTip.SetTip(arrow, caption);
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        flyout.Content = choices(flyout);
        arrow.Flyout = flyout;
        return HtmlOnly(new StackPanel { Orientation = Orientation.Horizontal, Children = { face, arrow } });
    }

    private Control ListSplit(string kind)
    {
        var numbering = kind == "numbering";
        var styles = numbering
            ? new[] { ("1. 2. 3.", "decimal"), ("1) 2) 3)", "decimal"), ("a. b. c.", "lower-alpha"), ("A. B. C.", "upper-alpha"), ("i. ii. iii.", "lower-roman"), ("I. II. III.", "upper-roman"), ("None", "none") }
            : new[] { ("● Filled round bullet", "disc"), ("○ Hollow round bullet", "circle"), ("■ Filled square bullet", "square"), ("None", "none") };
        var caption = numbering ? "Numbering" : "Bullets";
        var split = Split(numbering ? "OlIconNumbering" : "OlIconBullets", caption, () => Exec(kind), flyout => Choices(flyout,
            styles.Select(s => (s.Item1, (Action)(() => Exec(kind, s.Item2)))).ToArray()));
        var face = (Button)((StackPanel)split).Children[0];
        if (!_toggles.TryGetValue(kind, out var list)) _toggles[kind] = list = [];
        list.Add(face);
        return split;
    }

    private Control HighlightSplit() => Split("OlIconHighlight", "Text Highlight Color", () => Exec("hiliteColor", _lastHighlight), flyout =>
        Palette(flyout, HighlightColors, 5, ("No Color", "none"), hex => { if (hex != "none") _lastHighlight = hex; Exec("hiliteColor", hex); }));

    private Control FontColorSplit() => Split("OlIconFontColor", "Font Color", () => Exec("foreColor", _lastFontColor), flyout =>
        Palette(flyout, ThemeColors, 10, ("Automatic", "auto"), hex => { if (hex != "auto") _lastFontColor = hex; Exec("foreColor", hex); }));

    private Control ShadingSplit() => Split("OlIconShading", "Shading", () => Exec("shading", _lastShading), flyout =>
        Palette(flyout, ThemeColors, 10, ("No Color", "none"), hex => { if (hex != "none") _lastShading = hex; Exec("shading", hex); }));

    /// <summary>A grid of colour squares under one wide choice (Automatic / No Color).</summary>
    private static Control Palette(Flyout flyout, (string Name, string Hex)[] colors, int perRow, (string Label, string Value) top, Action<string> pick)
    {
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(2) };
        var first = new Button { Content = top.Label, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent, Focusable = false, Padding = new Thickness(6, 3) };
        first.Click += (_, _) => { flyout.Hide(); pick(top.Value); };
        panel.Children.Add(first);
        var grid = new UniformGrid { Columns = perRow };
        foreach (var (name, hex) in colors)
        {
            var swatch = new Button
            {
                Width = 20, Height = 20, Margin = new Thickness(2), Padding = new Thickness(0), Focusable = false, BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Gray, Background = new SolidColorBrush(Color.Parse(hex)), CornerRadius = new CornerRadius(0)
            };
            ToolTip.SetTip(swatch, name);
            swatch.Click += (_, _) => { flyout.Hide(); pick(hex); };
            grid.Children.Add(swatch);
        }
        panel.Children.Add(grid);
        return panel;
    }

    /// <summary>A list of text choices in a flyout.</summary>
    private static Control Choices(Flyout flyout, params (string Label, Action Do)[] items)
    {
        var panel = new StackPanel { MinWidth = 160 };
        foreach (var (label, action) in items)
        {
            var b = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Background = Brushes.Transparent, Focusable = false, Padding = new Thickness(8, 4) };
            b.Click += (_, _) => { flyout.Hide(); action(); };
            panel.Children.Add(b);
        }
        return panel;
    }

    /// <summary>A button that only opens a menu (Change Case, Line Spacing, Styles, Editing, Zoom). A null action is a planned entry, shown greyed.</summary>
    private Button Drop(Control face, string caption, params (string Header, Action? Do)[] items)
    {
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var (header, action) in items)
        {
            var item = new MenuItem { Header = action is null ? header + " (planned)" : header, IsEnabled = action is not null };
            if (action is not null) item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        var b = new Button { Content = face, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(3), Focusable = false, Flyout = menu, MinHeight = 24 };
        ToolTip.SetTip(b, caption);
        return b;
    }

    private Control SmallDropFace(string icon, string? text = null) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center,
        Children = { text is null ? Icon(icon, 16) : new TextBlock { Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center }, Chevron(7) }
    };

    private Control BigDropFace(string icon, string caption) => new StackPanel
    {
        Width = 62, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center,
        Children = { Icon(icon, 28), new TextBlock { Text = caption, FontSize = 11, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center }, Chevron(8) }
    };

    private Control ChangeCaseDrop() => HtmlOnly(Drop(SmallDropFace("", "Aa"), "Change Case",
        ("Sentence case.", () => Exec("changeCase", "sentence")), ("lowercase", () => Exec("changeCase", "lower")), ("UPPERCASE", () => Exec("changeCase", "upper")),
        ("Capitalize Each Word", () => Exec("changeCase", "capitalize")), ("tOGGLE cASE", () => Exec("changeCase", "toggle"))));

    private Control LineSpacingDrop() => HtmlOnly(Drop(SmallDropFace("OlIconLineSpacing"), "Line and Paragraph Spacing",
        new[] { "1.0", "1.15", "1.5", "2.0", "2.5", "3.0" }.Select(v => (v, (Action?)(() => Exec("lineSpacing", v.TrimEnd('0').TrimEnd('.'))))).ToArray()));

    private Control StylesDrop() => HtmlOnly(Drop(BigDropFace("OlIconStyles", "Styles"), "Styles",
        ("Normal", () => Exec("style", "normal")), ("Heading 1", () => Exec("style", "h1")), ("Heading 2", () => Exec("style", "h2")), ("Heading 3", () => Exec("style", "h3")),
        ("Title", () => Exec("style", "title")), ("Quote", () => Exec("style", "quote")), ("Code", () => Exec("style", "code"))));

    private Control EditingDrop() => HtmlOnly(Drop(BigDropFace("OlIconSearch", "Editing"), "Editing",
        ("Find…", () => _ = FindAsync()), ("Replace…", null), ("Select All", () => Exec("selectAll"))));

    private Control ZoomDrop() => Drop(BigDropFace("OlIconZoom", "Zoom"), "Zoom",
        new[] { 50, 75, 100, 125, 150, 200 }.Select(p => ($"{p}%", (Action?)(() => SetZoom(p / 100.0)))).ToArray());

    private void SetZoom(double factor)
    {
        _zoom = factor;
        _body.FontSize = 14 * factor;
        _editor?.Run("zoom", factor.ToString(CultureInfo.InvariantCulture));
        _status.Text = $"Zoom {factor:P0}.";
    }

    /// <summary>The HTML / Plain Text choice (Format Text and Options tabs); the current one is shown pressed.</summary>
    private Control FormatChoice(bool html)
    {
        var b = new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children =
            {
                new TextBlock { Text = "Aa", FontSize = 14, FontStyle = html ? FontStyle.Normal : FontStyle.Normal, FontFamily = html ? FontFamily.Default : new FontFamily("Consolas, Courier New, monospace"), VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = html ? "HTML" : "Plain Text", FontSize = 12, VerticalAlignment = VerticalAlignment.Center }
            } },
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(6, 3), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left, Focusable = false, CornerRadius = new CornerRadius(3)
        };
        ToolTip.SetTip(b, html ? "HTML: fonts, colours, lists, links and tables" : "Plain Text: text only, no formatting");
        b.Click += async (_, _) => await SetFormatAsync(html);
        _formatButtons.Add((b, html));
        return b;
    }

    private Control ClipboardGroup() => Group("Clipboard", BigDrop("OlIconPaste", "Paste", PasteAsync),
        Stack(Mini("OlIconCut", "Cut", () => CutCopyAsync(true)), Mini("OlIconCopy", "Copy", () => CutCopyAsync(false)), Mini("OlIconFormatPainter", "Format Painter", null)));

    /// <summary>The address / subject box that has the keyboard, or null when the message body has it.</summary>
    private TextBox? FocusedField() =>
        FocusManager?.GetFocusedElement() is TextBox { IsReadOnly: false } t && (t == _to || t == _cc || t == _bcc || t == _subject || (t == _body && !_isHtml)) ? t
        : !_isHtml ? _body : null;

    private async Task PasteAsync()
    {
        if (FocusedField() is { } box) { box.Paste(); return; }
        if (Clipboard is null) return;
        var text = await Clipboard.GetTextAsync();
        if (!string.IsNullOrEmpty(text)) Exec("insertText", text);
    }

    private async Task CutCopyAsync(bool cut)
    {
        if (FocusedField() is { } box) { if (cut) box.Cut(); else box.Copy(); return; }
        if (_editor is null || Clipboard is null) return;
        var text = await _editor.SelectedTextAsync();
        if (string.IsNullOrEmpty(text)) return;
        await Clipboard.SetTextAsync(text);
        if (cut) Exec("delete");
    }

    // ---------------------------------------------------------------- insert and find

    private async Task InsertLinkAsync()
    {
        if (!_isHtml) return;
        var answer = await PromptAsync("Insert Hyperlink", ("Text to display", ""), ("Address", "https://"));
        if (answer is null) return;
        var address = answer[1].Trim();
        if (!address.Contains(':', StringComparison.Ordinal)) address = address.Contains('@') ? "mailto:" + address : "https://" + address;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "mailto") || address is "https://" or "http://")
        {
            _status.Text = "Enter a web address (https://...) or an email address.";
            return;
        }
        Exec("link", uri.AbsoluteUri + "\n" + answer[0].Trim());
    }

    private async Task InsertTableAsync()
    {
        if (!_isHtml) return;
        var answer = await PromptAsync("Insert Table", ("Number of columns", "3"), ("Number of rows", "2"));
        if (answer is null) return;
        if (!int.TryParse(answer[0], out var columns) || !int.TryParse(answer[1], out var rows) || columns is < 1 or > 20 || rows is < 1 or > 50)
        {
            _status.Text = "A table has 1 to 20 columns and 1 to 50 rows.";
            return;
        }
        Exec("table", $"{rows},{columns}");
    }

    private async Task FindAsync()
    {
        if (_editor is null) return;
        var answer = await PromptAsync("Find", ("Find what", ""));
        if (answer is null || answer[0].Length == 0) return;
        _status.Text = await _editor.FindAsync(answer[0]) ? "" : $"\"{answer[0]}\" was not found.";
    }

    /// <summary>A small dialog with text boxes; null when cancelled.</summary>
    internal async Task<string[]?> PromptAsync(string title, params (string Label, string Initial)[] fields)
    {
        var dialog = new Window { Title = title + " — OpenOutlook", Width = 420, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        var boxes = fields.Select(f => new TextBox { Text = f.Initial }).ToArray();
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 72, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 72, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 6 };
        for (var i = 0; i < fields.Length; i++)
        {
            panel.Children.Add(new TextBlock { Text = fields[i].Label + ":" });
            panel.Children.Add(boxes[i]);
        }
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), Children = { ok, cancel } });
        dialog.Content = panel;
        dialog.Opened += (_, _) => { var first = boxes.FirstOrDefault(b => string.IsNullOrEmpty(b.Text)) ?? boxes.FirstOrDefault(); first?.Focus(); first?.SelectAll(); };
        if (PromptOverride is { } answer) return answer(title, fields.Select(f => f.Label).ToArray());
        return await dialog.ShowDialog<bool>(this) ? boxes.Select(b => b.Text ?? "").ToArray() : null;
    }

    private async Task<bool> ConfirmAsync(string title, string text)
    {
        if (ConfirmOverride is { } answer) return answer(text);
        var dialog = new Window { Title = title + " — OpenOutlook", Width = 440, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        var yes = new Button { Content = "Continue", IsDefault = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var no = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 14, Children =
            {
                new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { yes, no } }
            }
        };
        return await dialog.ShowDialog<bool>(this);
    }

    /// <summary>Test seams: answer the dialogs without showing them.</summary>
    internal Func<string, string[], string[]?>? PromptOverride { get; set; }
    internal Func<string, bool>? ConfirmOverride { get; set; }
}
