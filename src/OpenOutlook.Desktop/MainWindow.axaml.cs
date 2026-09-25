using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using OpenOutlook.JunkCleaner;
using PstCore;

namespace OpenOutlook.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly Dictionary<string, PstStore> _stores = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _readerGate = new(1, 1);
    private long _selectionVersion;
    private string? _activePath;
    private MailFolder? _activeFolder;
    private MailMessage? _activeMessage;
    private CancellationTokenSource? _searchCancellation;
    private readonly AppearanceSettingsStore _appearanceStore = new();
    private AppearanceSettings _appearance = new();
    private bool _appearanceReady;

    public MainWindow()
    {
        InitializeComponent();
        _appearance = _appearanceStore.Load();
        TextScaleSlider.Value = _appearance.TextSize;
        AccentPicker.SelectedIndex = _appearance.Accent switch
        {
            "Green" => 1, "Purple" => 2, "Orange" => 3, _ => 0
        };
        ApplyAppearance();
        _appearanceReady = true;
        Closed += (_, _) =>
        {
            _searchCancellation?.Cancel();
            // Pending reader tasks hold this gate; close is cooperative until they finish.
            _ = CloseStoresAsync();
        };
        Opened += async (_, _) =>
        {
            // Explicit command-line archives enable repeatable headless UI smoke tests.
            foreach (var path in Environment.GetCommandLineArgs().Skip(1).Where(File.Exists))
                await OpenArchiveAsync(Path.GetFullPath(path));
        };
    }

    private async void OpenPstClicked(object? sender, RoutedEventArgs e)
    {
        var chosen = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a PST archive read-only",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Outlook PST") { Patterns = ["*.pst"] }]
        });
        foreach (var file in chosen)
        {
            var path = file.TryGetLocalPath();
            if (path is not null) await OpenArchiveAsync(Path.GetFullPath(path));
        }
    }

    private async Task OpenArchiveAsync(string path)
    {
        if (_stores.ContainsKey(path)) return;
        StatusText.Text = $"Opening {Path.GetFileName(path)} read-only…";
        try
        {
            // Serialize reader use: NDB protects individual reads, not whole operations.
            await _readerGate.WaitAsync();
            PstStore store;
            try { store = await Task.Run(() => PstStore.Open(path, writable: false)); }
            finally { _readerGate.Release(); }
            if (_stores.TryAdd(path, store))
            {
                FolderTree.Items.Add(BuildArchiveNode(path, store));
                StatusText.Text = $"Opened {store.DisplayName} read-only.";
            }
            else store.Dispose();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not open {Path.GetFileName(path)}: {ex.Message}";
        }
    }

    private async Task CloseStoresAsync()
    {
        await _readerGate.WaitAsync();
        try
        {
            foreach (var store in _stores.Values) store.Dispose();
            _stores.Clear();
        }
        finally { _readerGate.Release(); }
    }

    private static TreeViewItem BuildArchiveNode(string path, PstStore store)
    {
        var root = new TreeViewItem { Header = store.DisplayName, Tag = path, IsExpanded = true };
        var visited = new HashSet<uint>();
        AddChildren(root, store.Root, path, visited);
        return root;
    }

    private static void AddChildren(ItemsControl parent, MailFolder folder, string path, HashSet<uint> visited)
    {
        if (!visited.Add(folder.Nid)) return;
        var item = new TreeViewItem { Header = folder.Name, Tag = new FolderSelection(path, folder), IsExpanded = folder.ParentNid == 0 };
        parent.Items.Add(item);
        foreach (var child in folder.Children) AddChildren(item, child, path, visited);
    }

    private async void FolderSelected(object? sender, SelectionChangedEventArgs e)
    {
        var version = Interlocked.Increment(ref _selectionVersion);
        _searchCancellation?.Cancel();
        if (FolderTree.SelectedItem is not TreeViewItem { Tag: FolderSelection selection })
        {
            _activePath = null;
            _activeFolder = null;
            MessageList.Items.Clear();
            ClearReader();
            return;
        }
        if (!_stores.TryGetValue(selection.Path, out var store)) return;
        _activePath = selection.Path;
        _activeFolder = selection.Folder;
        MessageList.Items.Clear();
        ClearReader();
        StatusText.Text = $"Loading {selection.Folder.Name}…";
        try
        {
            await _readerGate.WaitAsync();
            IReadOnlyList<MailSummary> messages;
            try
            {
                if (!_stores.TryGetValue(selection.Path, out var current) || !ReferenceEquals(current, store)) return;
                messages = await Task.Run(() => store.GetMessages(selection.Folder));
            }
            finally { _readerGate.Release(); }
            if (version != _selectionVersion) return;
            ShowMessages(messages);
            StatusText.Text = $"{messages.Count} messages · {store.DisplayName} · read-only";
        }
        catch (Exception ex) { if (version == _selectionVersion) StatusText.Text = $"Could not read folder: {ex.Message}"; }
    }

    private async void AccountSetupClicked(object? sender, RoutedEventArgs e)
    {
        // The setup panel deliberately does not request a password, secret, or pasted token.
        // Until account verification and a durable keyring-backed account model exist, keep
        // sign-in disabled rather than discarding a refresh token after browser authorization.
        var info = new Window
        {
            Title = "Connect an account — not yet available",
            Width = 490, Height = 240, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new TextBlock
            {
                Margin = new Thickness(20), TextWrapping = TextWrapping.Wrap,
                Text = "Microsoft personal and Google setup will use your system browser and a loopback PKCE callback. This build does not connect an account yet: account verification, keyring-backed refresh tokens and reconnect/removal are still being integrated. No password or pasted token is needed."
            }
        };
        await info.ShowDialog(this);
    }

    private async void PreviewJunkImportClicked(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Preview legacy OutlookJunkCleaner config (no import or cleaning)",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("JSON configuration") { Patterns = ["*.json"] }]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        try
        {
            var preview = await Task.Run(() => JunkCleanerSettingsStore.PreviewLegacyConfig(path));
            // Explicitly do not show or persist the private keyword strings in this preview shell.
            var ruleCount = (preview.Rules.DeleteHighImportance ? 1 : 0) +
                            (preview.Rules.DeleteMissingTo ? 1 : 0) +
                            (preview.Rules.DeleteOnBehalfOf ? 1 : 0);
            var dialog = new Window
            {
                Title = "Junk Cleaner configuration preview",
                Width = 470, Height = 250,
                Content = new TextBlock
                {
                    Margin = new Thickness(20), TextWrapping = TextWrapping.Wrap,
                    Text = $"{preview.Keywords.Count} private keywords, {ruleCount} optional rules, {preview.IntervalMinutes}-minute interval.\n\nLegacy automatic cleaning: {(preview.AlwaysClean ? "on" : "off")}. OpenOutlook cleaning stays OFF. Nothing was imported, connected to mail or deleted."
                }
            };
            await dialog.ShowDialog(this);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { StatusText.Text = "Could not preview the selected configuration file."; }
    }

    private async void SearchClicked(object? sender, RoutedEventArgs e)
    {
        if (_activePath is null || _activeFolder is null ||
            !_stores.TryGetValue(_activePath, out var store))
        {
            StatusText.Text = "Select an archive folder before searching.";
            return;
        }
        var query = ArchiveSearchBox.Text?.Trim() ?? "";
        if (query.Length == 0 || query.Length > 100)
        {
            StatusText.Text = "Enter 1–100 characters to search this folder's message headers.";
            return;
        }
        _searchCancellation?.Cancel();
        _searchCancellation = new CancellationTokenSource();
        var ct = _searchCancellation.Token;
        var path = _activePath;
        var folder = _activeFolder;
        var version = Interlocked.Increment(ref _selectionVersion);
        MessageList.Items.Clear();
        ClearReader();
        StatusText.Text = "Searching subject, sender and recipient in selected folder…";
        try
        {
            await _readerGate.WaitAsync(ct);
            IReadOnlyList<MailSummary> results;
            try
            {
                if (!_stores.TryGetValue(path, out var current) || !ReferenceEquals(store, current)) return;
                results = await Task.Run(() => store.GetMessages(folder)
                    .Where(m => m.Subject.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                m.From.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                m.To.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .Take(500).ToArray(), ct);
            }
            finally { _readerGate.Release(); }
            if (ct.IsCancellationRequested || version != _selectionVersion) return;
            ShowMessages(results);
            StatusText.Text = $"{results.Count} header matches (max 500) · {store.DisplayName} · read-only";
        }
        catch (OperationCanceledException) { /* New selection superseded this search. */ }
        catch (Exception ex) { if (version == _selectionVersion) StatusText.Text = $"Search failed: {ex.Message}"; }
    }

    private void ShowMessages(IReadOnlyList<MailSummary> messages)
    {
        foreach (var message in messages)
            MessageList.Items.Add(new ListBoxItem
            {
                Content = $"{(message.IsRead ? "  " : "● ")}{message.From}  |  {message.Subject}  |  {message.Received:g}",
                Tag = message
            });
    }

    private async void MessageSelected(object? sender, SelectionChangedEventArgs e)
    {
        var version = Interlocked.Increment(ref _selectionVersion);
        _activeMessage = null;
        ExportAttachmentButton.IsEnabled = false;
        ExportMessageButton.IsEnabled = false;
        if (MessageList.SelectedItem is not ListBoxItem { Tag: MailSummary summary } ||
            _activePath is null || !_stores.TryGetValue(_activePath, out var store)) return;
        StatusText.Text = "Reading message…";
        try
        {
            await _readerGate.WaitAsync();
            MailMessage message;
            try
            {
                if (_activePath is null || !_stores.TryGetValue(_activePath, out var current) ||
                    !ReferenceEquals(current, store)) return;
                message = await Task.Run(() => store.OpenMessage(summary));
            }
            finally { _readerGate.Release(); }
            if (version != _selectionVersion) return;
            _activeMessage = message;
            ExportAttachmentButton.IsEnabled = message.Attachments.Count > 0;
            ExportMessageButton.IsEnabled = message.Attachments.Count == 0;
            SubjectText.Text = message.Summary.Subject;
            SenderText.Text = $"From: {message.Summary.From}";
            RecipientText.Text = $"To: {message.Summary.To}";
            AttachmentText.Text = message.Attachments.Count == 0 ? "" :
                $"Attachments: {string.Join(", ", message.Attachments.Select(a => a.FileName))}";
            // Intentionally no raw HTML or network-capable renderer in this safety-first preview.
            BodyText.Text = string.IsNullOrWhiteSpace(message.BodyText)
                ? "(No plain-text body available; HTML rendering is not enabled in this preview.)"
                : message.BodyText;
            StatusText.Text = "Message opened read-only.";
        }
        catch (Exception ex) { if (version == _selectionVersion) StatusText.Text = $"Could not read message: {ex.Message}"; }
    }

    private async void ExportMessageClicked(object? sender, RoutedEventArgs e)
    {
        var message = _activeMessage;
        var path = _activePath;
        var version = _selectionVersion;
        if (message is null || path is null || !_stores.TryGetValue(path, out var store)) return;
        if (message.Attachments.Count > 0 || message.Summary.HasAttachment ||
            (string.IsNullOrEmpty(message.BodyText) && !string.IsNullOrEmpty(message.BodyHtml)))
        {
            StatusText.Text = "Text-only EML export cannot preserve this message's attachments or HTML-only body.";
            return;
        }
        // A directory picker avoids any provider-side creation or truncation of a target file.
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose folder for new text-only EML (attachments not supported)", AllowMultiple = false
        });
        if (version != _selectionVersion || !ReferenceEquals(_activeMessage, message)) return;
        var directory = folders.FirstOrDefault()?.TryGetLocalPath();
        if (directory is null) return;
        var output = Path.Combine(directory, $"message-{message.Summary.Nid:X8}.eml");
        ExportMessageButton.IsEnabled = false;
        try
        {
            await _readerGate.WaitAsync();
            try
            {
                if (version != _selectionVersion || !ReferenceEquals(_activeMessage, message) ||
                    !_stores.TryGetValue(path, out var current) || !ReferenceEquals(store, current)) return;
                await PstMessageEmlExporter.ExportAsync(store, message, output);
            }
            finally { _readerGate.Release(); }
            StatusText.Text = "Text-only EML saved to a new file; PST unchanged.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or NotSupportedException or InvalidOperationException)
        { StatusText.Text = "EML export failed or target already exists; no existing file was overwritten."; }
        finally { ExportMessageButton.IsEnabled = _activeMessage?.Attachments.Count == 0; }
    }

    private async void ExportAttachmentClicked(object? sender, RoutedEventArgs e)
    {
        var message = _activeMessage;
        var path = _activePath;
        var version = _selectionVersion;
        if (message is null || path is null || !_stores.TryGetValue(path, out var store) ||
            message.Attachments.Count == 0) return;
        // A separate attachment choice is required for messages with multiple files.
        var attachment = message.Attachments.Count == 1 ? message.Attachments[0] :
            await ChooseAttachmentAsync(message.Attachments);
        if (attachment is null || version != _selectionVersion || !ReferenceEquals(_activeMessage, message)) return;
        if (attachment.Method != 1 || attachment.Size < 0 || attachment.Size > PstAttachmentExporter.DefaultMaximumBytes)
        { StatusText.Text = "Only by-value attachments up to 64 MiB can be exported."; return; }
        string suggested;
        try { suggested = PstAttachmentExporter.ValidateSuggestedFileName(attachment.FileName); }
        catch (ArgumentException) { StatusText.Text = "This attachment has an unsafe filename and cannot be exported."; return; }
        // Choose a DIRECTORY rather than a SaveFilePicker target: some providers may
        // create/truncate an existing file as part of their picker flow before our no-overwrite guard.
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select where to export this attachment as a new file",
            AllowMultiple = false
        });
        if (version != _selectionVersion || !ReferenceEquals(_activeMessage, message)) return;
        var directory = folders.FirstOrDefault()?.TryGetLocalPath();
        if (directory is null) return;
        var output = Path.Combine(directory, suggested);
        ExportAttachmentButton.IsEnabled = false;
        try
        {
            await _readerGate.WaitAsync();
            try
            {
                if (version != _selectionVersion || !ReferenceEquals(_activeMessage, message) ||
                    !_stores.TryGetValue(path, out var current) || !ReferenceEquals(store, current)) return;
                await Task.Run(() => PstAttachmentExporter.ExportAsync(store, message.Summary, attachment, output));
            }
            finally { _readerGate.Release(); }
            StatusText.Text = "Attachment exported to a new file; PST was not changed.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException or InvalidOperationException or PstException)
        { StatusText.Text = "Attachment export failed or target already exists. No existing file was overwritten."; }
        finally { ExportAttachmentButton.IsEnabled = _activeMessage?.Attachments.Count > 0; }
    }

    private async Task<MailAttachment?> ChooseAttachmentAsync(IReadOnlyList<MailAttachment> attachments)
    {
        var chooser = new Window { Title = "Choose an attachment", Width = 450, Height = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var list = new ListBox();
        foreach (var item in attachments)
            list.Items.Add(new ListBoxItem { Content = item.FileName, Tag = item });
        var button = new Button { Content = "Export selected", Margin = new Thickness(8) };
        button.Click += (_, _) => chooser.Close((list.SelectedItem as ListBoxItem)?.Tag as MailAttachment);
        chooser.Content = new DockPanel { Children = { list, button } };
        DockPanel.SetDock(button, Dock.Bottom);
        return await chooser.ShowDialog<MailAttachment?>(this);
    }

    private async void DetachClicked(object? sender, RoutedEventArgs e)
    {
        var path = FolderTree.SelectedItem is TreeViewItem item ?
            item.Tag is FolderSelection folder ? folder.Path : item.Tag as string : null;
        if (path is null || !_stores.TryGetValue(path, out var store)) return;
        // The gate avoids disposing while a reader operation is in flight.
        try { await DetachAsync(path, store); }
        catch (Exception ex) { StatusText.Text = $"Could not detach: {ex.Message}"; }
    }

    private async Task DetachAsync(string path, PstStore store)
    {
        Interlocked.Increment(ref _selectionVersion);
        _searchCancellation?.Cancel();
        await _readerGate.WaitAsync();
        try
        {
            if (!_stores.Remove(path)) return;
            store.Dispose();
            foreach (var root in FolderTree.Items.OfType<TreeViewItem>().ToArray())
                if (Equals(root.Tag, path)) FolderTree.Items.Remove(root);
            _activePath = null;
            _activeFolder = null;
            MessageList.Items.Clear();
            ClearReader();
            StatusText.Text = $"Detached {Path.GetFileName(path)}; archive file was not deleted.";
        }
        finally { _readerGate.Release(); }
    }

    private void ToggleThemeClicked(object? sender, RoutedEventArgs e)
    {
        _appearance = _appearance with { DarkMode = !_appearance.DarkMode };
        ApplyAppearance();
        PersistAppearance();
    }

    private void AccentChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_appearanceReady || AccentPicker.SelectedItem is not ComboBoxItem item) return;
        _appearance = _appearance with { Accent = item.Content?.ToString() ?? "Blue" };
        ApplyAppearance();
        PersistAppearance();
    }

    private void TextScaleChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_appearanceReady) return;
        _appearance = _appearance with { TextSize = (int)Math.Round(e.NewValue) };
        ApplyAppearance();
        PersistAppearance();
    }

    private void ApplyAppearance()
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = _appearance.DarkMode ? ThemeVariant.Dark : ThemeVariant.Light;
        // These named high-contrast accents avoid arbitrary user hex colors with illegible foregrounds.
        var accent = _appearance.Accent switch
        {
            "Green" => "#176339", "Purple" => "#5A3186", "Orange" => "#895013", _ => "#174879"
        };
        TitleBand.Background = Brush.Parse(accent);
        ToolbarBand.Background = StatusBand.Background = Brush.Parse(_appearance.DarkMode ? "#202833" : "#EDF2F7");
        var size = _appearance.TextSize;
        FolderTree.FontSize = MessageList.FontSize = BodyText.FontSize = size;
        SubjectText.FontSize = size + 5;
        SenderText.FontSize = RecipientText.FontSize = AttachmentText.FontSize = size;
        StatusText.FontSize = size - 1;
        OpenPstButton.FontSize = DetachButton.FontSize = ThemeButton.FontSize = size;
        ArchiveSearchButton.FontSize = ExportAttachmentButton.FontSize = ExportMessageButton.FontSize = PreviewJunkImportButton.FontSize = AccountSetupButton.FontSize = ArchiveSearchBox.FontSize = AccentPicker.FontSize = size;
    }

    private void PersistAppearance()
    {
        try { _appearanceStore.Save(_appearance); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { StatusText.Text = "Could not save appearance settings; this session's choice still applies."; }
    }

    private void ClearReader()
    {
        _activeMessage = null;
        ExportAttachmentButton.IsEnabled = false;
        ExportMessageButton.IsEnabled = false;
        SubjectText.Text = "Select a message";
        SenderText.Text = RecipientText.Text = AttachmentText.Text = BodyText.Text = "";
    }

    private sealed record FolderSelection(string Path, MailFolder Folder);
}
