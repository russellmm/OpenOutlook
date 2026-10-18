using Avalonia;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using OpenOutlook.Providers.Google;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Desktop;

/// <summary>
/// The compose window, laid out like Outlook's: quick-access buttons, the Message / Insert / Options / Format Text / Review / Help ribbon, a large Send
/// button with the From account, To / Cc / Bcc and Subject rows, and the message body. It works with any <see cref="IComposeBackend"/> (Microsoft or Gmail);
/// the From list chooses the account to send from. Buttons whose feature is not built yet say "To be implemented".
/// </summary>
public sealed partial class ComposeWindow : Window
{
    private const string NotYet = "To be implemented";

    private readonly IReadOnlyList<ComposeAccount> _accounts;
    private ComposeAccount _account;
    private IComposeBackend _backend;
    private readonly ComboBox _from = new() { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _to = new() { Watermark = "name@example.com", BorderThickness = new Thickness(0, 0, 0, 1) };
    private readonly TextBox _cc = new() { BorderThickness = new Thickness(0, 0, 0, 1) };
    private readonly TextBox _bcc = new() { BorderThickness = new Thickness(0, 0, 0, 1) };
    private readonly TextBox _subject = new() { BorderThickness = new Thickness(0, 0, 0, 1) };
    private readonly TextBox _body = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Watermark = "Write your message" };
    private readonly Grid _bodyHost = new();
    private IHtmlEditorHost? _editor;
    private bool _isHtml;
    private readonly List<Control> _htmlOnly = [];                               // formatting controls: greyed in a plain-text message, as in Outlook
    private readonly Dictionary<string, List<Button>> _toggles = [];             // buttons that show the state at the cursor (Bold pressed, ...)
    private readonly List<ComboBox> _fontBoxes = [], _sizeBoxes = [];
    private readonly List<(Button Button, bool Html)> _formatButtons = [];
    private bool _syncingState;
    private string _lastHighlight = "#FFFF00", _lastFontColor = "#FF0000", _lastShading = "#D9D9D9";
    private double _zoom = 1;
    private readonly Grid _bccRow = new();
    private readonly ListBox _attachments = new() { MaxHeight = 90 };
    private readonly Border _attachmentBar = new() { IsVisible = false, Padding = new Thickness(0, 4) };
    private readonly TextBlock _titleText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 4) };
    private readonly Button _send = new() { MinWidth = 86, MinHeight = 96, HorizontalContentAlignment = HorizontalAlignment.Center };
    private readonly List<ComposeFile> _files = [];
    private readonly List<Control> _busyControls = [];
    private string? _draftId;
    private bool _busy, _loadFailed, _loadingDraft, _dirty, _closingAfterSave, _sent, _closingPromptOpen;
    private string _htmlBody = "";
    private ComposeDraft? _savedContent;
    private readonly ComposeSeed? _seed;
    private IReadOnlyList<RecipientEntry> _contacts = [], _recent = [], _directory = [];
    private readonly List<RecipientSuggestions> _suggestions = [];

    /// <summary>
    /// The account's contacts and the addresses it wrote to recently, for the To / Cc / Bcc suggestions and Select Names (set by the main window;
    /// null in tests that do not need them). Asked again when the From account changes.
    /// </summary>
    public Func<string, Task<(IReadOnlyList<RecipientEntry> Contacts, IReadOnlyList<RecipientEntry> Recent)>>? RecipientLookup { get; set; }
    /// <summary>After a send: the From address and every To / Cc / Bcc address (the main window adds them to Safe Senders when that is turned on).</summary>
    public event Action<string, IReadOnlyList<string>>? Sent;
    /// <summary>Where sent recipients are remembered (recent-recipients.json); tests point it elsewhere.</summary>
    public RecentRecipientsStore RecentRecipients { get; set; } = new();

    public ComposeWindow(IReadOnlyList<ComposeAccount> accounts, ComposeAccount initial, ComposeSeed? seed = null, string? draftId = null)
    {
        _accounts = accounts;
        _account = initial;
        _backend = initial.CreateBackend();
        _seed = seed;
        _draftId = draftId;
        Width = 1000; Height = 760; MinWidth = 640; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Closing += ComposeClosing;

        foreach (var box in new[] { _to, _cc, _bcc, _subject, _body })
            box.TextChanged += (_, _) => { if (!_loadingDraft) _dirty = true; UpdateTitle(); };

        Content = BuildLayout();
        if (seed is not null) ApplySeed(seed);
        UpdateBodyMode();
        UpdateTitle();
        RefreshAttachments();
        if (_draftId is not null) Opened += async (_, _) => await LoadDraftAsync();
        Opened += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_to.Text)) { if (_isHtml) _editor?.FocusBody(); else _body.Focus(); }
            else _to.Focus();
        };
        Opened += async (_, _) => await LoadRecipientsAsync();
        AddHandler(KeyDownEvent, (_, e) =>                                           // Ctrl+K: Check Names, as in Outlook
        {
            if (e.Key == Avalonia.Input.Key.K && e.KeyModifiers == Avalonia.Input.KeyModifiers.Control) { CheckNames(); e.Handled = true; }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        foreach (var box in new[] { _to, _cc, _bcc }) _suggestions.Add(new RecipientSuggestions(box, () => _directory));
    }

    /// <summary>Contacts and recent addresses of the From account (never fails the window: problems leave the lists as they were).</summary>
    internal async Task LoadRecipientsAsync()
    {
        _recent = RecentRecipients.Load(_account.Address);
        _directory = RecipientDirectory.Merge(_contacts, _recent);
        if (RecipientLookup is null) return;
        try
        {
            var (contacts, recent) = await RecipientLookup(_account.Address);
            _contacts = contacts; _recent = recent;
            _directory = RecipientDirectory.Merge(_contacts, _recent);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { AppLog.Note("compose", "contacts / recent recipients not loaded: " + e.Message); }
    }

    /// <summary>To / Cc / Bcc buttons and Address Book: Outlook's Select Names dialog; OK adds its three boxes to the message.</summary>
    internal async Task OpenSelectNamesAsync(string field)
    {
        var dialog = new SelectNamesWindow(_account.Address, _contacts, RecipientDirectory.Merge([], _recent), field);
        await dialog.ShowDialog(this);
        if (dialog.Result is not { } chosen) return;
        ApplySelectedNames(chosen.To, chosen.Cc, chosen.Bcc);
    }

    internal void ApplySelectedNames(string to, string cc, string bcc)
    {
        if (!string.IsNullOrWhiteSpace(to)) _to.Text = RecipientDirectory.Append(_to.Text, RecipientDirectory.Addresses(to));
        if (!string.IsNullOrWhiteSpace(cc)) _cc.Text = RecipientDirectory.Append(_cc.Text, RecipientDirectory.Addresses(cc));
        if (!string.IsNullOrWhiteSpace(bcc)) { _bcc.Text = RecipientDirectory.Append(_bcc.Text, RecipientDirectory.Addresses(bcc)); _bccRow.IsVisible = true; }
    }

    /// <summary>Check Names (ribbon, Ctrl+K, and before Send): names typed in To / Cc / Bcc become addresses. False when some could not be resolved.</summary>
    internal bool CheckNames()
    {
        var problems = new List<string>();
        foreach (var box in new[] { _to, _cc, _bcc })
        {
            if (string.IsNullOrWhiteSpace(box.Text)) continue;
            var (text, unresolved) = RecipientDirectory.Resolve(box.Text, _directory);
            if (!(box.Text ?? "").Trim().TrimEnd(';').Equals(text.Trim().TrimEnd(';'), StringComparison.Ordinal)) box.Text = text;
            problems.AddRange(unresolved);
        }
        _status.Text = problems.Count == 0 ? "All names are resolved." : "Check Names could not resolve " + string.Join(", ", problems) + ". Type the full address or use To / Cc to pick it.";
        return problems.Count == 0;
    }

    internal IReadOnlyList<RecipientEntry> Directory => _directory;
    internal RecipientSuggestions SuggestionsFor(string field) => _suggestions[field switch { "Cc" => 1, "Bcc" => 2, _ => 0 }];
    internal TextBox FieldBox(string field) => field switch { "Cc" => _cc, "Bcc" => _bcc, _ => _to };

    /// <summary>After a send: its To / Cc / Bcc addresses become the first suggestions next time (never fails the send).</summary>
    private void RememberRecipients(ComposeDraft content)
    {
        try
        {
            var used = new[] { content.To, content.Cc, content.Bcc }.SelectMany(RecipientDirectory.Addresses)
                .Select(a => (_directory.FirstOrDefault(d => string.Equals(d.Address, a, StringComparison.OrdinalIgnoreCase))?.Name ?? "", a));
            RecentRecipients.Record(_account.Address, used, DateTimeOffset.UtcNow);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AppLog.Note("compose", "recent recipients not saved: " + e.Message); }
        Sent?.Invoke(_account.Address, new[] { content.To, content.Cc, content.Bcc }.SelectMany(RecipientDirectory.Addresses).ToList());
    }

    // ---------------------------------------------------------------- layout

    private Control BuildLayout()
    {
        var root = new DockPanel { Background = Brushes.WhiteSmoke };
        var top = new StackPanel();
        top.Children.Add(QuickAccess());
        top.Children.Add(Ribbon());
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);
        root.Children.Add(new Border
        {
            Background = Brushes.White, Margin = new Thickness(8, 4, 8, 0), CornerRadius = new CornerRadius(4), Child = Message()
        });
        return root;
    }

    private Control QuickAccess()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(8, 4, 8, 0) };
        bar.Children.Add(Icon("OlIconAppMark", 22));
        bar.Children.Add(Small("OlIconSave", "Save draft", async () => await SaveAsync(false)));
        bar.Children.Add(Small("OlIconUndo", "Undo", () => { if (_isHtml) _editor?.Run("undo"); else _body.Undo(); return Task.CompletedTask; }));
        bar.Children.Add(Small("OlIconRedo", "Redo", () => { if (_isHtml) _editor?.Run("redo"); else _body.Redo(); return Task.CompletedTask; }));
        bar.Children.Add(Small("OlIconBackArrow", null, null));
        bar.Children.Add(Small("OlIconChevronDown", null, null));
        bar.Children.Add(_titleText);
        return bar;
    }

    /// <summary>One per ribbon tab: collapses groups that do not fit (for tests).</summary>
    internal IReadOnlyList<RibbonResponsiveLayout> RibbonLayouts { get; private set; } = [];

    private TabControl Ribbon()
    {
        var tabs = new TabControl { Padding = new Thickness(0), Margin = new Thickness(4, 0), Background = Brushes.Transparent };
        if (Application.Current?.Styles is not null) tabs.Classes.Add("olTabs");
        void Tab(string name, params Control[] groups)
        {
            var strip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2), Spacing = 0 };
            foreach (var g in groups) strip.Children.Add(g);
            tabs.Items.Add(new TabItem { Header = name, Content = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = strip } });
        }
        Tab("File", Group("File", Big(null, "Save", async () => await SaveAsync(false)), Big(null, "Discard", () => { _closingAfterSave = true; Close(); return Task.CompletedTask; }), Big(null, "Print", null)));
        Tab("Message",
            ClipboardGroup(),
            // Outlook's two rows: font, size, grow / shrink, lists, clear formatting; then bold / italic / underline, highlight and colour, alignment, indents
            Group("Basic Text", Stack(
                Row(FontBox(), SizeBox(), Cmd("OlIconGrowFont", "Grow Font", "grow"), Cmd("OlIconShrinkFont", "Shrink Font", "shrink"), Sep(),
                    ListSplit("bullets"), ListSplit("numbering"), Sep(), Cmd("OlIconClearFormatting", "Clear All Formatting", "clear")),
                Row(Toggle(null, "Bold", "bold", "B"), Toggle(null, "Italic", "italic", "I"), Toggle(null, "Underline", "underline", "U"), Sep(),
                    HighlightSplit(), FontColorSplit(), Sep(),
                    Toggle("OlIconAlignLeft", "Align Left", "justifyLeft"), Toggle("OlIconAlignCenter", "Center", "justifyCenter"), Toggle("OlIconAlignRight", "Align Right", "justifyRight"), Sep(),
                    Cmd("OlIconDecreaseIndent", "Decrease Indent", "outdent"), Cmd("OlIconIncreaseIndent", "Increase Indent", "indent")))),
            Group("Names", Big("OlIconAddressBook", "Address Book", () => OpenSelectNamesAsync("To")), Big("OlIconCheckNames", "Check Names", () => { CheckNames(); return Task.CompletedTask; })),
            Group("Include", Stack(Labeled("OlIconPaperclip", "Attach File", async () => await AddAttachmentsAsync()), Labeled("OlIconSignature", "Signature", null))),
            Group("Tags", Stack(Mini("OlIconFlag", "Follow Up", null, dropDown: true), Mini("OlIconImportanceHigh", "High Importance", null), Mini("OlIconImportanceLow", "Low Importance", null))),
            Group("Apps", Big("OlIconAllApps", "All Apps", null)),
            Group("Immersive", Big("OlIconReadAloud", "Immersive Reader", null)),
            Group("My Templates", Big("OlIconTemplate", "View Templates", null)));
        Tab("Insert",
            Group("Include", Big("OlIconPaperclip", "Attach File", async () => await AddAttachmentsAsync()), Big(null, "Attach Item", null), Big(null, "Business Card", null), Big(null, "Calendar", null), Big("OlIconSignature", "Signature", null)),
            Group("Tables", HtmlOnly(Big("OlIconTable", "Table", InsertTableAsync))),
            Group("Illustrations", HtmlOnly(Big("OlIconPicture", "Pictures", InsertPicturesAsync)), Big(null, "Shapes", null), Big(null, "Icons", null)),
            Group("Links", HtmlOnly(Big("OlIconLink", "Link", InsertLinkAsync)), Big(null, "Bookmark", null)),
            Group("Symbols", Big(null, "Equation", null), Big(null, "Symbol", null), HtmlOnly(Big("OlIconHorizontalLine", "Horizontal Line", () => { _editor?.Run("line"); return Task.CompletedTask; }))));
        var bccToggle = Big(null, "Bcc", () => { _bccRow.IsVisible = !_bccRow.IsVisible; return Task.CompletedTask; });
        Tab("Options",
            Group("Show Fields", bccToggle, Big(null, "From", null)),
            Group("Format", Stack(FormatChoice(true), FormatChoice(false)), Big(null, "Themes", null), Big(null, "Colors", null), Big(null, "Fonts", null)),
            Group("Tracking", Stack(Mini(null, "Request a Delivery Receipt", null), Mini(null, "Request a Read Receipt", null))),
            Group("More Options", Big(null, "Delay Delivery", null), Big(null, "Direct Replies To", null)));
        // Format Text as Outlook lays it out (no Rich Text: OpenOutlook writes HTML or plain text)
        Tab("Format Text",
            ClipboardGroup(),
            Group("Format", Stack(FormatChoice(true), FormatChoice(false))),
            Group("Font", Stack(
                Row(FontBox(), SizeBox()),
                Row(Toggle(null, "Bold", "bold", "B"), Toggle(null, "Italic", "italic", "I"), Toggle(null, "Underline", "underline", "U"), Toggle("OlIconStrikethrough", "Strikethrough", "strikeThrough"),
                    Toggle("OlIconSubscript", "Subscript", "subscript"), Toggle("OlIconSuperscript", "Superscript", "superscript"), Sep(), Cmd("OlIconClearFormatting", "Clear All Formatting", "clear")),
                Row(HighlightSplit(), FontColorSplit(), Sep(), Cmd("OlIconGrowFont", "Grow Font", "grow"), Cmd("OlIconShrinkFont", "Shrink Font", "shrink"), Sep(), ChangeCaseDrop()))),
            Group("Paragraph", Stack(
                Row(ListSplit("bullets"), ListSplit("numbering"), Mini("OlIconMultilevelList", "Multilevel List", null, dropDown: true), Sep(),
                    Cmd("OlIconDecreaseIndent", "Decrease Indent", "outdent"), Cmd("OlIconIncreaseIndent", "Increase Indent", "indent")),
                Row(Toggle("OlIconAlignLeft", "Align Left", "justifyLeft"), Toggle("OlIconAlignCenter", "Center", "justifyCenter"), Toggle("OlIconAlignRight", "Align Right", "justifyRight"),
                    Toggle("OlIconAlignJustify", "Justify", "justifyFull"), Sep(), LineSpacingDrop()),
                Row(ShadingSplit(), Mini("OlIconBorders", "Borders", null, dropDown: true), Sep(), Mini("OlIconSort", "Sort", null), Toggle("OlIconShowMarks", "Show/Hide ¶", "marks")))),
            Group("Styles", StylesDrop(), Big("OlIconChangeStyles", "Change Styles", null)),
            Group("Editing", EditingDrop()),
            Group("Zoom", ZoomDrop()));
        Tab("Review", Group("Proofing", Big(null, "Spelling & Grammar", null), Big(null, "Thesaurus", null), Big(null, "Word Count", null)),
            Group("Speech", Big("OlIconReadAloud", "Read Aloud", null)), Group("Accessibility", Big(null, "Check Accessibility", null)),
            Group("Language", Big("OlIconTranslate", "Translate", null), Big(null, "Language", null)));
        Tab("Help", Group("Help", Big("OlIconHelp", "Help", null), Big(null, "Contact Support", null), Big(null, "Feedback", null)));
        tabs.SelectedIndex = 1;
        RibbonLayouts = RibbonResponsiveLayout.AttachAll(tabs);           // groups collapse into drop-down buttons from the right as the window narrows, as in the main window
        return tabs;
    }

    private Control Message()
    {
        var grid = new Grid { Margin = new Thickness(12, 6), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,*"), ColumnDefinitions = new ColumnDefinitions("Auto,*") };

        _send.Content = Stack2("OlIconSendReceive", "Send");
        _send.Click += async (_, _) => await SaveAsync(true);
        Grid.SetRowSpan(_send, 4);
        _send.Margin = new Thickness(0, 0, 14, 0);
        grid.Children.Add(_send);
        _busyControls.Add(_send);

        // From
        foreach (var a in _accounts)
            _from.Items.Add(new ComboBoxItem { Content = a.ToString(), Tag = a, IsEnabled = a.CanSend });
        _from.SelectedIndex = Math.Max(0, _accounts.ToList().IndexOf(_account));
        _from.SelectionChanged += FromChanged;
        _busyControls.Add(_from);
        var fromRow = new Grid { ColumnDefinitions = new ColumnDefinitions("72,*"), Margin = new Thickness(0, 0, 0, 4) };
        fromRow.Children.Add(Pill("From", "Choose the account to send from"));
        Grid.SetColumn(_from, 1);
        fromRow.Children.Add(_from);
        Place(grid, fromRow, 1, 0);

        Place(grid, FieldRow("To", "Select names from your contacts or recent recipients", _to), 1, 1);
        Place(grid, FieldRow("Cc", "Select names from your contacts or recent recipients", _cc), 1, 2);
        var bcc = FieldRow("Bcc", "Select names from your contacts or recent recipients", _bcc);
        _bccRow.Children.Add(bcc);
        _bccRow.IsVisible = false;
        Place(grid, _bccRow, 1, 3);
        // The Send button spans four rows; the subject row sits under the address rows.
        var subjectRow = new Grid { ColumnDefinitions = new ColumnDefinitions("72,*"), Margin = new Thickness(0, 4, 0, 4) };
        subjectRow.Children.Add(new TextBlock { Text = "Subject", VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center });
        Grid.SetColumn(_subject, 1);
        subjectRow.Children.Add(_subject);
        Grid.SetColumnSpan(subjectRow, 2);
        Place(grid, subjectRow, 0, 4);

        _attachmentBar.Child = AttachmentBar();
        Grid.SetColumnSpan(_attachmentBar, 2);
        Place(grid, _attachmentBar, 0, 5);

        _bodyHost.Children.Add(_body);                                              // plain text; the HTML editor joins it the first time the message is HTML
        var bodyBorder = new Border { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(0, 1, 0, 0), Child = _bodyHost, Margin = new Thickness(0, 2, 0, 0) };
        Grid.SetColumnSpan(bodyBorder, 2);
        Place(grid, bodyBorder, 0, 6);
        return grid;
    }

    private Control AttachmentBar()
    {
        var remove = new Button { Content = "Remove selected", VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 0, 0, 0) };
        remove.Click += async (_, _) => await RemoveAttachmentAsync();
        _busyControls.Add(remove);
        var panel = new DockPanel();
        DockPanel.SetDock(remove, Dock.Right);
        panel.Children.Add(remove);
        panel.Children.Add(new StackPanel { Children = { new TextBlock { Text = "Attached", Foreground = Brushes.Gray, FontSize = 12 }, _attachments } });
        return panel;
    }

    private static void Place(Grid grid, Control c, int column, int row)
    {
        Grid.SetColumn(c, column); Grid.SetRow(c, row);
        grid.Children.Add(c);
    }

    private Control FieldRow(string label, string tip, TextBox box)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("72,*"), Margin = new Thickness(0, 0, 0, 4) };
        var pill = Pill(label, tip);
        pill.Click += async (_, _) => await OpenSelectNamesAsync(label);
        row.Children.Add(pill);
        Grid.SetColumn(box, 1);
        row.Children.Add(box);
        _busyControls.Add(box);
        return row;
    }

    private static Button Pill(string text, string tip)
    {
        var b = new Button { Content = text, MinWidth = 62, Margin = new Thickness(0, 0, 8, 0), HorizontalContentAlignment = HorizontalAlignment.Center, CornerRadius = new CornerRadius(4) };
        ToolTip.SetTip(b, tip);
        return b;
    }

    // ---- ribbon building blocks ----

    private Control Group(string caption, params Control[] content)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var c in content) strip.Children.Add(c);
        var dock = new DockPanel();
        var label = new TextBlock { Text = caption, Classes = { "olGroupLabel" }, FontSize = 11, Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
        DockPanel.SetDock(label, Dock.Bottom);
        dock.Children.Add(label);
        dock.Children.Add(strip);
        return new Border { Classes = { "olGroup" }, BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(4, 2, 6, 0), Margin = new Thickness(0, 0, 4, 0), Child = dock };
    }

    private Button Big(string? icon, string caption, Func<Task>? action)
    {
        var content = new StackPanel { Width = 62, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(icon is not null ? Icon(icon, 28) : new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(4), BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center });
        content.Children.Add(new TextBlock { Text = caption, FontSize = 11, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center });
        return Wire(new Button { Content = content, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(2) }, caption, action);
    }

    private Button BigDrop(string icon, string caption, Func<Task>? action)
    {
        var b = Big(icon, caption, action);
        ((StackPanel)b.Content!).Children.Add(Chevron(8));
        return b;
    }

    /// <summary>A small ribbon button: an icon (with a drop-down arrow for Outlook's split buttons) or a letter such as B / I / U; the name is in its tooltip.</summary>
    private Button Mini(string? icon, string caption, Func<Task>? action, string? glyph = null, FontWeight? weight = null, FontStyle style = FontStyle.Normal, bool dropDown = false)
    {
        Control face = icon is not null ? Icon(icon, 16)
            : glyph is not null ? new TextBlock { Text = glyph, FontSize = 14, FontWeight = weight ?? FontWeight.Normal, FontStyle = style, Width = 16, TextAlignment = TextAlignment.Center, TextDecorations = glyph == "U" ? TextDecorations.Underline : null }
            : new TextBlock { Text = caption, FontSize = 11 };
        object content = dropDown ? new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1, Children = { face, Chevron(7) } } : face;
        var b = Wire(new Button { Content = content, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(3), MinHeight = 24, MinWidth = 24, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center }, caption, action);
        if (action is not null) ToolTip.SetTip(b, caption);
        else ToolTip.SetTip(b, caption + " - " + NotYet);
        return b;
    }

    /// <summary>A small button with its name beside the icon (Attach File, Signature).</summary>
    private Button Labeled(string icon, string caption, Func<Task>? action) =>
        Wire(new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { Icon(icon, 16), new TextBlock { Text = caption, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }, Chevron(7) } },
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(4, 3), HorizontalAlignment = HorizontalAlignment.Left
        }, caption, action);

    private static Control Chevron(double size) => Icon("OlIconChevronDown", size);

    /// <summary>The thin divider between button clusters inside Basic Text.</summary>
    private static Control Sep() => new Border { Width = 1, Height = 18, Background = Brushes.Gainsboro, Margin = new Thickness(3, 0), VerticalAlignment = VerticalAlignment.Center };

    private Button Small(string icon, string? caption, Func<Task>? action)
    {
        var b = new Button { Content = Icon(icon, 18), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(4) };
        return Wire(b, caption ?? icon.Replace("OlIcon", ""), action, caption is null);
    }

    private Button Wire(Button b, string caption, Func<Task>? action, bool forceNotYet = false)
    {
        b.Focusable = false;                                                         // as in Outlook, the ribbon leaves the keyboard where it was (the body keeps its selection)
        if (action is null || forceNotYet)
        {
            ToolTip.SetTip(b, NotYet);
            b.Click += (_, _) => _status.Text = $"{caption}: {NotYet.ToLowerInvariant()}.";
        }
        else
        {
            ToolTip.SetTip(b, caption);
            b.Click += async (_, _) => await action();
            _busyControls.Add(b);
        }
        return b;
    }

    private Control Real(Control c) { _busyControls.Add(c); return c; }

    private static StackPanel Stack(params Control[] rows) => new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center }.With(rows);
    private static StackPanel Row(params Control[] items) => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 }.With(items);

    private Control Stack2(string icon, string caption) => new StackPanel
    {
        Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        Children = { Icon(icon, 34), new TextBlock { Text = caption, HorizontalAlignment = HorizontalAlignment.Center } }
    };

    private static Control Icon(string key, double size)
    {
        if (Application.Current is { } app && app.TryFindResource(key, out var template) && template is Avalonia.Controls.Templates.IControlTemplate ct)
            return OlIcon.Create(ct, size);
        return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(3), BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1) };
    }

    private TextBox? Focused() => (FocusManager?.GetFocusedElement() as TextBox) is { IsReadOnly: false } t ? t : _body;

    // ---------------------------------------------------------------- account choice

    private void FromChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_from.SelectedItem is not ComboBoxItem { Tag: ComposeAccount chosen } || ReferenceEquals(chosen, _account)) return;
        if (_draftId is not null)
        {
            _status.Text = $"This message is already saved as a draft in {_account.Address}. Start a new message to send from {chosen.Address}.";
            _from.SelectedIndex = Math.Max(0, _accounts.ToList().IndexOf(_account));
            return;
        }
        _account = chosen;
        _backend = chosen.CreateBackend();
        _contacts = [];
        _ = LoadRecipientsAsync();
        _dirty = true;
        _status.Text = $"Sending from {chosen.Address}.";
        UpdateTitle();
    }

    // ---------------------------------------------------------------- body mode

    private void UpdateTitle()
    {
        var subject = string.IsNullOrWhiteSpace(_subject.Text) ? "Untitled" : _subject.Text!.Trim();
        Title = $"{subject}  -  Message ({(_isHtml ? "HTML" : "Plain Text")})";
        _titleText.Text = Title;
    }

    private void ApplySeed(ComposeSeed seed)
    {
        _loadingDraft = true;
        try
        {
            _to.Text = seed.To; _cc.Text = seed.Cc; _subject.Text = seed.Subject;
            _bccRow.IsVisible = false;
            if (seed.Html) ShowHtmlBody(seed.Body);
            else _body.Text = seed.Body;
            if (seed.Files is not null) _files.AddRange(seed.Files);
        }
        finally { _loadingDraft = false; }
        _dirty = false;
        _savedContent = null;
    }

    // ---------------------------------------------------------------- draft operations

    private async Task LoadDraftAsync()
    {
        SetBusy(true);
        _status.Text = "Loading draft…";
        try
        {
            _loadingDraft = true;
            var loaded = await _backend.LoadAsync(_draftId!);
            _to.Text = loaded.Draft.To; _cc.Text = loaded.Draft.Cc; _bcc.Text = loaded.Draft.Bcc; _subject.Text = loaded.Draft.Subject;
            _bccRow.IsVisible = !string.IsNullOrWhiteSpace(loaded.Draft.Bcc);
            if (loaded.Draft.Html) ShowHtmlBody(HtmlEditorDocument.Unwrap(ComposeHtml.Sanitize(loaded.Draft.Body)));
            else { _isHtml = false; _body.Text = loaded.Draft.Body; }
            _files.AddRange(loaded.Files);
            UpdateBodyMode();
            RefreshAttachments();
            _status.Text = "Edit this draft, then save or send it.";
        }
        catch (Exception ex)
        {
            _loadFailed = true;
            _status.Text = "Could not open this draft: " + SafeError(ex);
        }
        finally
        {
            _loadingDraft = false;
            if (!_loadFailed) _savedContent = CurrentContent();
            _dirty = false;
            SetBusy(false);
        }
    }

    private async Task AddAttachmentsAsync()
    {
        try
        {
            var selected = await SafePick.FilesAsync(this, new FilePickerOpenOptions { Title = "Attach files", AllowMultiple = true }, failure => _status.Text = failure);
            foreach (var item in selected)
            {
                var path = item.TryGetLocalPath();
                if (path is null) continue;
                var length = new FileInfo(path).Length;
                var total = _files.Sum(f => f.Size) + length;
                if (length > _backend.MaxAttachmentBytes || (_backend.Kind == "Gmail" && total > GmailMimeBuilder.MaxMessageBytes))
                {
                    _status.Text = $"{item.Name} would exceed the {_backend.MaxAttachmentBytes / (1024 * 1024)} MB attachment limit of {_backend.Kind}.";
                    continue;
                }
                _files.Add(new ComposeFile { Name = item.Name, Path = path, Size = length, MimeType = MimeFor(item.Name) });
                _dirty = true;
            }
            RefreshAttachments();
        }
        catch (Exception ex) { _status.Text = "Could not attach a file: " + SafeError(ex); }
    }

    private static string MimeFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf", ".txt" => "text/plain", ".html" or ".htm" => "text/html", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif", ".zip" => "application/zip", ".doc" => "application/msword", ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xls" => "application/vnd.ms-excel", ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".csv" => "text/csv", _ => "application/octet-stream"
    };

    private async Task RemoveAttachmentAsync()
    {
        if (_busy || _attachments.SelectedItem is not ComposeFile selected) return;
        if (selected.ServerId is null || _draftId is null || _backend.Kind != "Microsoft")
        {
            _files.Remove(selected);
            _dirty = true;
            RefreshAttachments();
            return;
        }
        SetBusy(true);
        try
        {
            await _backend.RemoveServerFileAsync(_draftId, selected.ServerId);
            _files.Remove(selected);
            RefreshAttachments();
            _status.Text = "Attachment removed from the draft.";
        }
        catch (Exception ex) { _status.Text = "Could not remove attachment: " + SafeError(ex); }
        finally { SetBusy(false); }
    }

    /// <summary>Saves the draft, or (send = true) sends the message.</summary>
    private async Task<bool> SaveAsync(bool send)
    {
        if (_busy || _loadFailed) return false;
        if (!_account.CanSend)
        {
            _status.Text = $"{_account.Address} cannot send: {_account.DisabledReason}.";
            return false;
        }
        if (send && string.IsNullOrWhiteSpace(_to.Text) && string.IsNullOrWhiteSpace(_cc.Text) && string.IsNullOrWhiteSpace(_bcc.Text))
        {
            _status.Text = "Enter at least one recipient before sending.";
            return false;
        }
        if (send && !CheckNames()) return false;                                   // as Outlook: typed names become addresses first
        SetBusy(true);
        await FlushEditorAsync();
        var progress = new Progress<string>(text => _status.Text = text);
        try
        {
            var content = CurrentContent();
            if (send)
            {
                await _backend.SendAsync(_draftId, content, _files, progress);
                _sent = true;
                RememberRecipients(content);
                Close();
                return true;
            }
            var saved = await _backend.SaveAsync(_draftId, content, _files, progress);
            _draftId = saved.DraftId;
            if (saved.ServerFiles is { } server)
            {
                _files.RemoveAll(f => f.ServerId is null);
                _files.RemoveAll(f => f.ServerId is not null);
                _files.AddRange(server);
            }
            RefreshAttachments();
            _dirty = false;
            _savedContent = content;
            _status.Text = _backend.SavedMessage;
            return true;
        }
        catch (Exception ex)
        {
            _status.Text = "Mail action failed: " + SafeError(ex) + (_draftId is null || send ? "" : " The draft was not updated.");
            return false;
        }
        finally { SetBusy(false); }
    }

    private async void ComposeClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_sent || _closingAfterSave) return;
        if (_closingPromptOpen) { e.Cancel = true; return; }
        if (_busy)
        {
            e.Cancel = true;
            _status.Text = "Wait for the current draft operation to finish before closing.";
            return;
        }
        if (!HasUnsavedChanges()) return;
        e.Cancel = true;
        if (_account.CanSend && await SaveAsync(false))
        {
            _closingAfterSave = true;
            Close();
        }
        else
            await OfferDiscardAfterFailedSaveAsync();
    }

    private async Task OfferDiscardAfterFailedSaveAsync()
    {
        _closingPromptOpen = true;
        try
        {
            var dialog = new Window { Title = "Draft could not be saved — OpenOutlook", Width = 470, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var keep = new Button { Content = "Keep editing" };
            var discard = new Button { Content = "Discard and close" };
            keep.Click += (_, _) => dialog.Close(false);
            discard.Click += (_, _) => dialog.Close(true);
            dialog.Content = new StackPanel
            {
                Margin = new Thickness(18), Spacing = 14, Children =
                {
                    new TextBlock { Text = "OpenOutlook could not save this draft. Keep it open to retry, or discard your unsaved changes and close.", TextWrapping = TextWrapping.Wrap },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { keep, discard } }
                }
            };
            if (await dialog.ShowDialog<bool>(this))
            {
                _closingAfterSave = true;
                Close();
            }
        }
        finally { _closingPromptOpen = false; }
    }

    private void RefreshAttachments()
    {
        _attachments.ItemsSource = _files.ToArray();
        _attachmentBar.IsVisible = _files.Count > 0;
    }

    /// <summary>The message as it is saved and sent; an HTML body carries the default font in its wrapper (<see cref="HtmlEditorDocument.Wrap"/>).</summary>
    private ComposeDraft CurrentContent() =>
        new(_to.Text ?? "", _cc.Text ?? "", _bcc.Text ?? "", _subject.Text ?? "",
            _isHtml ? HtmlEditorDocument.Wrap(_htmlBody) : _body.Text ?? "", _isHtml);

    private bool HasUnsavedChanges()
    {
        if (_dirty || _files.Any(f => !f.Saved)) return true;
        var current = CurrentContent();
        if (_savedContent is not null) return current != _savedContent;
        var body = _isHtml ? _htmlBody : _body.Text ?? "";                          // an untouched reply keeps its seed body exactly
        var blank = _isHtml ? HtmlEditorDocument.IsBlank(body) : string.IsNullOrWhiteSpace(body);
        return !string.IsNullOrWhiteSpace(current.To) || !string.IsNullOrWhiteSpace(current.Cc) || !string.IsNullOrWhiteSpace(current.Bcc) ||
               !string.IsNullOrWhiteSpace(current.Subject) || !blank && body != (_seed?.Body ?? "");
    }

    private static string PreviewText(string html)
    {
        try { return string.Concat(SafeHtmlPreview.Parse(html).Select(run => run.Text)); }
        catch (Exception) { return "(Formatted message)"; }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (var control in _busyControls) control.IsEnabled = !busy && !_loadFailed && (control != _send || _account.CanSend);
        UpdateBodyMode();
    }

    private static string SafeError(Exception ex) => ex switch
    {
        GraphMailException error => error.Message,
        GmailReadException error => error.Message,
        ArgumentException error => error.Message,
        IOException error => error.Message,
        NotSupportedException error => error.Message,
        _ => "Check the connection and account permissions, then retry."
    };

    // test seams
    internal string StatusText => _status.Text ?? "";
    internal bool DraftSaved => _draftId is not null;
    internal ComposeAccount SelectedAccount => _account;
    internal ComposeDraft Draft => CurrentContent();
    internal IReadOnlyList<ComposeFile> Files => _files;
}

internal static class PanelExtensions
{
    public static StackPanel With(this StackPanel panel, IEnumerable<Control> children)
    {
        foreach (var c in children) panel.Children.Add(c);
        return panel;
    }
}
