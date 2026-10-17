using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using OpenOutlook.JunkCleaner;

namespace OpenOutlook.Desktop;

/// <summary>
/// Outlook's Junk Email Options dialog (docs/junk-lists-plan.md): Options, Safe Senders, Safe Recipients, Blocked Senders and International
/// tabs; Add / Edit / Remove / Import from File / Export to File; OK, Cancel and Apply. The lists shown are the ones in effect (read from
/// Outlook plus OpenOutlook's own); Apply hands the edited account to <c>save</c>.
/// </summary>
public sealed class JunkEmailOptionsWindow : Window
{
    private static readonly JunkListKind[] Kinds = [JunkListKind.SafeSenders, JunkListKind.SafeRecipients, JunkListKind.BlockedSenders, JunkListKind.BlockedTopLevelDomains];
    /// <summary>Saves: applies the given change to the account as it is stored now (the right-click menu may have changed it meanwhile) and returns the result.</summary>
    private readonly Func<Func<JunkListsAccount, JunkListsAccount>, JunkListsAccount> _save;
    private readonly Dictionary<JunkListKind, List<string>> _loaded = [];
    private readonly Dictionary<JunkListKind, string> _filters = [];
    private readonly Func<Task<(JunkListsAccount Account, string Message)>>? _readFromOutlook;
    private JunkListsAccount _account;
    private readonly Dictionary<JunkListKind, List<string>> _lists = [];
    private readonly Dictionary<JunkListKind, ListBox> _boxes = [];
    private readonly Dictionary<JunkListKind, List<TextBlock>> _counts = [];
    private readonly CheckBox _trustContacts = new() { Content = "Also trust email from my _Contacts" };
    private readonly CheckBox _applyLists = new() { Content = "_Apply these lists in OpenOutlook: move mail from safe senders out of Junk Email, and mail from blocked senders into it" };
    private readonly TextBlock _outlookStatus = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock _outlookLevel = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _apply = new() { Content = "Apply", MinWidth = 100, IsEnabled = false };
    private readonly TabControl _tabs = new();
    private bool _loading;

    /// <summary>Set by the Add / Edit prompt in tests instead of a dialog.</summary>
    internal Func<string, string?, Task<string?>>? PromptOverride { get; set; }
    internal string LastMessage { get; private set; } = "";

    public JunkEmailOptionsWindow(string address, JunkListsAccount account, Func<Func<JunkListsAccount, JunkListsAccount>, JunkListsAccount> save,
        Func<Task<(JunkListsAccount Account, string Message)>>? readFromOutlook = null)
    {
        _account = account; _save = save; _readFromOutlook = readFromOutlook;
        Title = "Junk Email Options - " + address;
        Width = 640; Height = 680; MinWidth = 560; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Themed.Brush("OlCard", Brushes.White);
        DenseLists(this);
        Styles.Add(new Avalonia.Styling.Style(x => x.OfType<TabItem>())
        { Setters = { new Avalonia.Styling.Setter(TabItem.PaddingProperty, new Thickness(8, 4)), new Avalonia.Styling.Setter(TabItem.MinHeightProperty, 0.0) } });

        _tabs.Items.Add(new TabItem { Header = Tab("Options"), Content = OptionsTab() });
        _tabs.Items.Add(new TabItem { Header = Tab("Safe Senders"), Content = ListTab(JunkListKind.SafeSenders,
            "Email from addresses or domain names on your Safe Senders List will never be treated as junk email.", _trustContacts,
            new CheckBox { Content = "Automatically add people I email to the Safe Senders List", IsEnabled = false, [ToolTip.TipProperty] = "Planned Feature" }) });
        _tabs.Items.Add(new TabItem { Header = Tab("Safe Recipients"), Content = ListTab(JunkListKind.SafeRecipients,
            "Email sent to addresses or domain names on your Safe Recipients List will never be treated as junk email.") });
        _tabs.Items.Add(new TabItem { Header = Tab("Blocked Senders"), Content = ListTab(JunkListKind.BlockedSenders,
            "Email from addresses or domain names on your Blocked Senders List will always be treated as junk email.") });
        _tabs.Items.Add(new TabItem { Header = Tab("International"), Content = InternationalTab() });

        var ok = new Button { Content = "OK", MinWidth = 100, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 100, IsCancel = true };
        ok.Click += (_, _) => { ApplyChanges(); Close(); };
        cancel.Click += (_, _) => Close();
        _apply.Click += (_, _) => ApplyChanges();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12), Children = { ok, cancel, _apply } };
        var dock = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(buttons);
        dock.Children.Add(new Border { Margin = new Thickness(12, 12, 12, 0), Child = _tabs });
        Content = dock;
        _trustContacts.IsCheckedChanged += (_, _) => Changed();
        _applyLists.IsCheckedChanged += (_, _) => Changed();
        Load(account);
    }

    /// <summary>The account as last applied.</summary>
    public JunkListsAccount Account => _account;
    internal IReadOnlyList<string> Shown(JunkListKind kind) => _lists[kind];
    /// <summary>What the list control shows (after the search box).</summary>
    internal IReadOnlyList<string> Visible(JunkListKind kind) => _boxes[kind].ItemsSource?.OfType<string>().ToList() ?? [];
    internal void Search(JunkListKind kind, string text) { _searchBoxes[kind].Text = text; _filters[kind] = text; Refresh(kind); }
    private readonly Dictionary<JunkListKind, TextBox> _searchBoxes = [];
    internal bool HasChanges => _apply.IsEnabled;
    internal void SelectTab(int index) => _tabs.SelectedIndex = index;
    internal void Select(JunkListKind kind, params string[] entries) { _boxes[kind].SelectedItems!.Clear(); foreach (var e in entries) _boxes[kind].SelectedItems!.Add(e); }

    private void Load(JunkListsAccount account)
    {
        _loading = true;
        var effective = account.Effective;
        foreach (var kind in Kinds) { _lists[kind] = effective.Get(kind).ToList(); _loaded[kind] = _lists[kind].ToList(); }
        _trustContacts.IsChecked = account.TrustContacts;
        _applyLists.IsChecked = account.ApplyLists;
        foreach (var kind in Kinds) Refresh(kind);
        _outlookLevel.Text = account.OutlookOptions is { } o
            ? $"Microsoft's junk email protection for this account, as set in Outlook: {o.Level}." + (o.PermanentlyDelete ? " Suspected junk email is deleted for good by Microsoft." : "")
            : "Microsoft's junk email protection level is set in Outlook or on Outlook.com (Settings > Mail > Junk email); other programs cannot read or change it.";
        _outlookStatus.Text = account.OutlookReadAt is { } at
            ? $"Lists read from Outlook on {at.ToLocalTime():g} ({account.FromOutlook.SafeSenders.Count} safe senders, {account.FromOutlook.SafeRecipients.Count} safe recipients, {account.FromOutlook.BlockedSenders.Count} blocked senders), from {account.OutlookSource}. " +
              "They are read again when OpenOutlook starts while Outlook is closed and Outlook has changed them."
            : "Outlook's lists have not been read on this computer. OpenOutlook reads them from Outlook desktop's offline file when it starts while Outlook is closed (Windows), or import the files Outlook's Export to File writes.";
        _loading = false;
        _apply.IsEnabled = false;
    }

    private void Changed() { if (!_loading) _apply.IsEnabled = true; }

    /// <summary>
    /// Saves what was added and removed in this window since it was opened (or last applied), on top of the lists as they are stored now:
    /// an entry the right-click menu added meanwhile is kept (it was lost before 2026-10-17 when the window saved its whole, older copy).
    /// </summary>
    internal void ApplyChanges()
    {
        if (!_apply.IsEnabled) return;
        var added = Kinds.ToDictionary(k => k, k => _lists[k].Except(_loaded[k]).ToList());
        var removed = Kinds.ToDictionary(k => k, k => _loaded[k].Except(_lists[k]).ToHashSet());
        var trust = _trustContacts.IsChecked == true; var apply = _applyLists.IsChecked == true;
        var saved = _save(stored =>
        {
            var account = stored;
            foreach (var kind in Kinds)
                account = account.WithEdited(kind, stored.Effective.Get(kind).Where(e => !removed[kind].Contains(e)).Concat(added[kind]));
            return account with { TrustContacts = trust, ApplyLists = apply };
        });
        _account = saved;
        Load(saved);
    }

    private Control OptionsTab()
    {
        var read = new Button { Content = "Read Outlook's lists now", Margin = new Thickness(0, 4, 0, 0), IsEnabled = _readFromOutlook is not null };
        ToolTip.SetTip(read, "Reads Safe Senders, Safe Recipients and Blocked Senders from Outlook desktop's offline file. Close Outlook first; the file is only read.");
        read.Click += async (_, _) => await ReadFromOutlookAsync();
        return Page(
            Text("OpenOutlook can move messages that appear to be junk email into the Junk Email folder."),
            _outlookLevel,
            new CheckBox { Content = "Permanently delete suspected junk email instead of moving it to the Junk Email folder", IsEnabled = false, [ToolTip.TipProperty] = "OpenOutlook never deletes mail for good; the Junk Cleaner moves it to Deleted Items." },
            _applyLists,
            new Border { Height = 1, Background = Themed.Brush("OlHairline", Brushes.LightGray), Margin = new Thickness(0, 6) },
            _outlookStatus, read);
    }

    internal async Task ReadFromOutlookAsync()
    {
        if (_readFromOutlook is null) return;
        if (_apply.IsEnabled) ApplyChanges();
        var (account, message) = await _readFromOutlook();
        _account = account;
        Load(account);
        LastMessage = message;
        _outlookStatus.Text = message + " " + _outlookStatus.Text;
    }

    private Control InternationalTab()
    {
        var tlds = new Button { Content = "Blocked Top-Level Domain List...", HorizontalAlignment = HorizontalAlignment.Center };
        tlds.Click += async (_, _) => await EditTopLevelDomainsAsync();
        var encodings = new Button { Content = "Blocked Encodings List...", HorizontalAlignment = HorizontalAlignment.Center, IsEnabled = false };
        ToolTip.SetTip(encodings, "Planned Feature");
        return Page(
            Text("Some email messages you receive might be written in languages you are unfamiliar with and don't want to read. These messages can be marked as junk and moved to the Junk Email folder."),
            Text("The sender's email address in different countries/regions can end with top-level domain codes, such as .ca, .mx, or .us. The Blocked Top-Level Domain List allows you to block all messages sent from an email address ending with a specific top-level domain."),
            tlds, CountLabel(JunkListKind.BlockedTopLevelDomains, HorizontalAlignment.Center),
            Text("Characters of each language are contained in a special encoding or character set. The Blocked Encoding List allows you to block all email messages in a specific encoding."),
            encodings);
    }

    internal Window? TopLevelDomainDialog { get; private set; }

    internal async Task EditTopLevelDomainsAsync()
    {
        var dialog = new Window
        {
            Title = "Blocked Top-Level Domain List", Width = 420, Height = 460, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Themed.Brush("OlCard", Brushes.White),
        };
        DenseLists(dialog);
        TopLevelDomainDialog = dialog;
        var close = new Button { Content = "OK", MinWidth = 90, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
        close.Click += (_, _) => dialog.Close();
        ToolTip.SetTip(close, "Back to Junk Email Options; OK or Apply there saves the list");
        var dock = new DockPanel();
        DockPanel.SetDock(close, Dock.Bottom);
        dock.Children.Add(close);
        dock.Children.Add(ListTab(JunkListKind.BlockedTopLevelDomains, "Email from addresses ending with these top-level domains (for example ru for .ru) is treated as junk email."));
        dialog.Content = dock;
        Refresh(JunkListKind.BlockedTopLevelDomains);
        await dialog.ShowDialog(this);
    }

    private Control ListTab(JunkListKind kind, string intro, params Control[] below)
    {
        var box = new ListBox { SelectionMode = SelectionMode.Multiple, Height = 300, Padding = new Thickness(0), Background = Themed.Brush("OlCard", Brushes.White) };
        _boxes[kind] = box;
        var search = new TextBox { Watermark = "Search this list", Text = _filters.GetValueOrDefault(kind, ""), Margin = new Thickness(0, 0, 0, 6) };
        search.TextChanged += (_, _) => { _filters[kind] = search.Text ?? ""; Refresh(kind); };
        _searchBoxes[kind] = search;
        var count = CountLabel(kind, HorizontalAlignment.Left);
        var add = Btn("Add..."); var edit = Btn("Edit..."); var remove = Btn("Remove");
        var import = Btn("Import from File..."); var export = Btn("Export to File...");
        add.Click += async (_, _) => await AddAsync(kind);
        edit.Click += async (_, _) => await EditAsync(kind);
        remove.Click += (_, _) => Remove(kind);
        import.Click += async (_, _) => await ImportAsync(kind);
        export.Click += async (_, _) => await ExportAsync(kind);
        box.SelectionChanged += (_, _) => { edit.IsEnabled = box.SelectedItems?.Count == 1; remove.IsEnabled = box.SelectedItems?.Count > 0; };
        edit.IsEnabled = remove.IsEnabled = false;
        var side = new StackPanel { Spacing = 8, Width = 150, Margin = new Thickness(12, 0, 0, 0), Children = { add, edit, remove, new Border { Height = 150 }, import, export } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var left = new DockPanel();
        DockPanel.SetDock(search, Dock.Top);
        left.Children.Add(search);
        left.Children.Add(new Border { BorderBrush = Themed.Brush("OlHairline", Brushes.LightGray), BorderThickness = new Thickness(1), Child = box });
        grid.Children.Add(left);
        Grid.SetColumn(side, 1); grid.Children.Add(side);
        var page = Page([Text(intro), grid, count, .. below]);
        return page;
    }

    private TextBlock CountLabel(JunkListKind kind, HorizontalAlignment align)
    {
        var label = new TextBlock { FontSize = 12, HorizontalAlignment = align };
        if (!_counts.TryGetValue(kind, out var labels)) _counts[kind] = labels = [];
        labels.Add(label);
        return label;
    }

    /// <summary>Outlook's lists are dense: one line per entry, no padding band around the list (used by this window and its dialogs).</summary>
    private static void DenseLists(Window window) =>
        window.Styles.Add(new Avalonia.Styling.Style(x => x.OfType<ListBoxItem>())
        { Setters = { new Avalonia.Styling.Setter(ListBoxItem.PaddingProperty, new Thickness(6, 1)), new Avalonia.Styling.Setter(ListBoxItem.MinHeightProperty, 0.0) } });

    private static TextBlock Tab(string text) => new() { Text = text, FontSize = 13.5 };

    private static Button Btn(string text) => new() { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
    private static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
    private static Control Page(params Control[] children)
    {
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(14) };
        foreach (var c in children) panel.Children.Add(c);
        return new ScrollViewer { Content = panel };
    }

    private void Refresh(JunkListKind kind)
    {
        var filter = _filters.GetValueOrDefault(kind, "").Trim();
        var shown = filter.Length == 0 ? _lists[kind] : _lists[kind].Where(e => e.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (_boxes.TryGetValue(kind, out var box)) box.ItemsSource = shown.ToList();
        var fromOutlook = _account.FromOutlook.Get(kind).Intersect(_lists[kind]).Count();
        foreach (var count in _counts.GetValueOrDefault(kind) ?? [])
            count.Text = (filter.Length > 0 ? $"{shown.Count} of " : "") + $"{_lists[kind].Count} entr{(_lists[kind].Count == 1 ? "y" : "ies")}" + (fromOutlook > 0 ? $", {fromOutlook} read from Outlook" : "") +
                (filter.Length > 0 ? $" (search: \"{filter}\")" : "");
    }

    private void Set(JunkListKind kind, IEnumerable<string> entries)
    {
        _lists[kind] = JunkLists.Normalize(kind, entries).ToList();
        Refresh(kind);
        Changed();
    }

    internal async Task AddAsync(JunkListKind kind)
    {
        var text = await PromptAsync("Add address or domain", null);
        if (text is null) return;
        if (JunkLists.NormalizeEntry(kind, text) is not { } entry) { LastMessage = $"\"{text}\" is not a valid email address or domain name."; await TellAsync(LastMessage); return; }
        Set(kind, _lists[kind].Append(entry));
    }

    internal async Task EditAsync(JunkListKind kind)
    {
        if (_boxes[kind].SelectedItems?.OfType<string>().FirstOrDefault() is not { } old) return;
        var text = await PromptAsync("Edit address or domain", old);
        if (text is null) return;
        if (JunkLists.NormalizeEntry(kind, text) is not { } entry) { LastMessage = $"\"{text}\" is not a valid email address or domain name."; await TellAsync(LastMessage); return; }
        Set(kind, _lists[kind].Where(e => e != old).Append(entry));
    }

    internal void Remove(JunkListKind kind)
    {
        var chosen = _boxes[kind].SelectedItems?.OfType<string>().ToHashSet() ?? [];
        if (chosen.Count > 0) Set(kind, _lists[kind].Where(e => !chosen.Contains(e)));
    }

    /// <summary>Adds the entries of an Outlook export file (one per line). Returns how many were new.</summary>
    internal int ImportBytes(JunkListKind kind, byte[] bytes)
    {
        var entries = JunkLists.ParseTextFile(kind, bytes);
        var added = entries.Except(_lists[kind]).Count();
        Set(kind, _lists[kind].Concat(entries));
        LastMessage = $"{added} new entr{(added == 1 ? "y" : "ies")} imported ({entries.Count} in the file).";
        return added;
    }

    internal byte[] ExportBytes(JunkListKind kind) => JunkLists.ToTextFile(_lists[kind]);

    private async Task ImportAsync(JunkListKind kind)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import from File", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Text files") { Patterns = ["*.txt"] }, FilePickerFileTypes.All],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        try
        {
            if (new FileInfo(path).Length > 16 * 1024 * 1024) { await TellAsync("That file is too large for a junk list."); return; }
            ImportBytes(kind, await File.ReadAllBytesAsync(path));
            await TellAsync(LastMessage);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { await TellAsync("The file could not be read: " + e.Message); }
    }

    private async Task ExportAsync(JunkListKind kind)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export to File", DefaultExtension = "txt", SuggestedFileName = kind switch
            {
                JunkListKind.SafeSenders => "Safe Senders.txt", JunkListKind.SafeRecipients => "Safe Recipients.txt",
                JunkListKind.BlockedSenders => "Blocked Senders.txt", _ => "Blocked Top-Level Domains.txt",
            },
            FileTypeChoices = [new FilePickerFileType("Text files") { Patterns = ["*.txt"] }],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        try { await File.WriteAllBytesAsync(path, ExportBytes(kind)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { await TellAsync("The file could not be written: " + e.Message); }
    }

    private async Task<string?> PromptAsync(string title, string? value)
    {
        if (PromptOverride is not null) return await PromptOverride(title, value);
        var box = new TextBox { Text = value ?? "", Watermark = "someone@example.com, @example.com or example.com" };
        var dialog = new Window
        {
            Title = title, Width = 440, Height = 170, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Themed.Brush("OlCard", Brushes.White),
        };
        var ok = new Button { Content = "OK", MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true };
        ok.Click += (_, _) => dialog.Close(box.Text);
        cancel.Click += (_, _) => dialog.Close(null);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(14), Spacing = 10, Children =
            {
                Text("Enter an email address or Internet domain name to be added to the list."), box,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
            }
        };
        dialog.Opened += (_, _) => box.Focus();
        return await dialog.ShowDialog<string?>(this);
    }

    private async Task TellAsync(string message)
    {
        if (PromptOverride is not null) return;                                                    // headless tests read LastMessage
        var dialog = new Window
        {
            Title = "Junk Email Options", Width = 400, Height = 150, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Themed.Brush("OlCard", Brushes.White),
        };
        var ok = new Button { Content = "OK", MinWidth = 90, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel { Margin = new Thickness(14), Spacing = 12, Children = { Text(message), ok } };
        await dialog.ShowDialog(this);
    }
}
