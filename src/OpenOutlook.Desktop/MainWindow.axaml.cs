using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using OpenOutlook.Auth;
using OpenOutlook.JunkCleaner;
using OpenOutlook.Providers.Microsoft;
using PstCore;

namespace OpenOutlook.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly Dictionary<string, PstStore> _stores = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Path, uint FolderNid), IReadOnlyList<MailSummary>> _folderCache = new();
    private readonly Queue<(string Path, uint FolderNid)> _folderCacheOrder = new();
    private readonly SemaphoreSlim _readerGate = new(1, 1);
    private readonly ConnectedAccountRegistry _accountRegistry = new();
    private readonly LibsecretSecretStore _secrets = new();
    private readonly HttpClient _tokenHttp = DesktopOAuth.CreateHttpClient();
    private readonly HttpClient _graphHttp = GraphInboxReader.CreateSecureHttpClient();
    private readonly Dictionary<string, MicrosoftMailSession> _microsoftSessions = new(StringComparer.Ordinal);
    private CancellationTokenSource? _onlineCancellation;
    private CancellationTokenSource? _folderDiscoveryCancellation;
    private ConnectedAccount? _activeMicrosoftAccount;
    private MicrosoftFolderSelection? _activeMicrosoftFolder;
    private IReadOnlyList<GraphInboxMessage>? _currentGraphMessages;
    private long _folderVersion;
    private long _messageVersion;
    private IReadOnlyList<MailSummary>? _currentMessages;
    private bool _suppressGroupToggle;
    private bool _updatingMessageList;
    private string? _activePath;
    private MailFolder? _activeFolder;
    private MailMessage? _activeMessage;
    private string _bodyPlain = "";
    private bool _showRichBody = true;
    private IReadOnlyList<HtmlPreviewRun>? _richRuns;
    private CancellationTokenSource? _searchCancellation;
    private readonly AppearanceSettingsStore _appearanceStore = new();
    private readonly ViewLayoutSettingsStore _viewLayoutStore = new();
    private AppearanceSettings _appearance = new();

    public MainWindow()
    {
        InitializeComponent();
        _appearance = _appearanceStore.Load();
        ApplyAppearance();
        ApplyViewLayout(_viewLayoutStore.Load());
        GroupByDateCheck.IsCheckedChanged += GroupByDateChanged;
        Closed += (_, _) =>
        {
            PersistViewLayout();
            _searchCancellation?.Cancel();
            _onlineCancellation?.Cancel();
            _folderDiscoveryCancellation?.Cancel();
            _tokenHttp.Dispose();
            _graphHttp.Dispose();
            // Pending reader tasks hold this gate; close is cooperative until they finish.
            _ = CloseStoresAsync();
        };
        Opened += async (_, _) =>
        {
            // Explicit command-line archives enable repeatable headless UI smoke tests.
            foreach (var path in Environment.GetCommandLineArgs().Skip(1).Where(File.Exists))
                await OpenArchiveAsync(Path.GetFullPath(path));
            RefreshConnectedAccounts();
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
        var roots = PstFolderPresentation.VisibleRoots(store.Root);
        var countVisited = new HashSet<uint>();
        var unread = roots.Sum(folder => CountUnread(folder, countVisited));
        var root = new TreeViewItem { Header = FolderHeader(store.DisplayName, unread), Tag = path, IsExpanded = true };
        var visited = new HashSet<uint>();
        foreach (var folder in roots)
            AddChildren(root, folder, path, visited);
        return root;
    }

    private static long CountUnread(MailFolder folder, HashSet<uint> visited)
    {
        if (!visited.Add(folder.Nid)) return 0;
        return Math.Max(0, folder.UnreadCount) + folder.Children.Sum(child => CountUnread(child, visited));
    }

    private static object FolderHeader(string name, long unread)
    {
        if (unread <= 0)
            return new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis,
                [ToolTip.TipProperty] = name };
        return new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = name, MaxWidth = 140, TextTrimming = TextTrimming.CharacterEllipsis,
                    [ToolTip.TipProperty] = name },
                new TextBlock { Text = unread.ToString("N0"), FontWeight = FontWeight.SemiBold }
            }
        };
    }

    private static void AddChildren(ItemsControl parent, MailFolder folder, string path, HashSet<uint> visited)
    {
        if (!visited.Add(folder.Nid)) return;
        var item = new TreeViewItem { Header = FolderHeader(folder.Name, folder.UnreadCount), Tag = new FolderSelection(path, folder), IsExpanded = folder.ParentNid == 0 };
        parent.Items.Add(item);
        foreach (var child in folder.Children) AddChildren(item, child, path, visited);
    }

    private async void FolderSelected(object? sender, SelectionChangedEventArgs e)
    {
        var version = Interlocked.Increment(ref _folderVersion);
        Interlocked.Increment(ref _messageVersion);
        _searchCancellation?.Cancel();
        _onlineCancellation?.Cancel();
        _activeMicrosoftAccount = null;
        _activeMicrosoftFolder = null;
        _currentGraphMessages = null;
        RefreshInboxButton.IsEnabled = false;
        if (FolderTree.SelectedItem is TreeViewItem { Tag: MicrosoftFolderSelection online })
        {
            _activePath = null;
            _activeFolder = null;
            _currentMessages = null;
            MessageList.ItemsSource = null;
            ClearReader();
            _activeMicrosoftAccount = online.Account;
            _activeMicrosoftFolder = online;
            RefreshInboxButton.IsEnabled = true;
            _onlineCancellation = new CancellationTokenSource();
            await LoadMicrosoftFolderAsync(online, version, _onlineCancellation.Token);
            return;
        }
        if (FolderTree.SelectedItem is not TreeViewItem { Tag: FolderSelection selection })
        {
            _activePath = null;
            _activeFolder = null;
            _currentMessages = null;
            MessageList.ItemsSource = null;
            ClearReader();
            return;
        }
        if (!_stores.TryGetValue(selection.Path, out var store)) return;
        _activePath = selection.Path;
        _activeFolder = selection.Folder;
        _currentMessages = null;
        MessageList.ItemsSource = null;
        ClearReader();
        var cacheKey = (selection.Path, selection.Folder.Nid);
        if (_folderCache.TryGetValue(cacheKey, out var cached))
        {
            ShowMessages(cached);
            StatusText.Text = $"{cached.Count} messages · {store.DisplayName} · read-only";
            return;
        }
        StatusText.Text = $"Loading {selection.Folder.Name}…";
        try
        {
            await _readerGate.WaitAsync();
            IReadOnlyList<MailSummary> messages;
            try
            {
                if (version != _folderVersion) return;
                if (!_stores.TryGetValue(selection.Path, out var current) || !ReferenceEquals(current, store)) return;
                messages = await Task.Run(() => store.GetMessages(selection.Folder));
            }
            finally { _readerGate.Release(); }
            if (version != _folderVersion) return;
            CacheFolder(cacheKey, messages);
            ShowMessages(messages);
            StatusText.Text = $"{messages.Count} messages · {store.DisplayName} · read-only";
        }
        catch (Exception ex) { if (version == _folderVersion) StatusText.Text = $"Could not read folder: {ex.Message}"; }
    }

    private async void AccountSetupClicked(object? sender, RoutedEventArgs e)
    {
        await new AccountSetupWindow().ShowDialog(this);
        RefreshConnectedAccounts();
    }

    private void RefreshConnectedAccounts()
    {
        _folderDiscoveryCancellation?.Cancel();
        _folderDiscoveryCancellation = new CancellationTokenSource();
        foreach (var existing in FolderTree.Items.OfType<TreeViewItem>()
            .Where(item => item.Tag is ConnectedAccount).ToArray())
            FolderTree.Items.Remove(existing);
        _microsoftSessions.Clear();
        try
        {
            foreach (var account in _accountRegistry.Load().Where(account => account.Provider == OAuthProvider.MicrosoftConsumers))
            {
                var root = new TreeViewItem { Header = account.DisplayAddress, Tag = account, IsExpanded = true };
                root.Items.Add(new TreeViewItem { Header = "Inbox", Tag = new MicrosoftFolderSelection(account, "inbox", "Inbox") });
                FolderTree.Items.Add(root);
                _ = LoadAccountFoldersAsync(account, root, _folderDiscoveryCancellation.Token);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        { StatusText.Text = "Saved account settings could not be read."; }
    }

    private async Task LoadAccountFoldersAsync(ConnectedAccount account, TreeViewItem root,
        CancellationToken cancellationToken)
    {
        try
        {
            var token = await GetMicrosoftSession(account).GetAccessTokenAsync(cancellationToken);
            var folders = await new GraphMailFolderReader(_graphHttp, account.AccountId)
                .GetFoldersAsync(token, cancellationToken);
            if (cancellationToken.IsCancellationRequested || !FolderTree.Items.Contains(root)) return;
            foreach (var folder in folders)
            {
                if (folder.DisplayName.Equals("Inbox", StringComparison.OrdinalIgnoreCase))
                {
                    if (root.Items.FirstOrDefault() is TreeViewItem inbox)
                    {
                        inbox.Header = FolderHeader(folder.DisplayName, folder.UnreadCount);
                        foreach (var child in folder.Children)
                            inbox.Items.Add(BuildMicrosoftFolderNode(account, child));
                    }
                }
                else root.Items.Add(BuildMicrosoftFolderNode(account, folder));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!cancellationToken.IsCancellationRequested && FolderTree.Items.Contains(root) &&
                _activeMicrosoftAccount is null)
                StatusText.Text = "Could not list all account folders. Inbox is still available.";
        }
    }

    private static TreeViewItem BuildMicrosoftFolderNode(ConnectedAccount account, GraphMailboxFolder folder)
    {
        var item = new TreeViewItem
        {
            Header = FolderHeader(folder.DisplayName, folder.UnreadCount),
            Tag = new MicrosoftFolderSelection(account, folder.Id, folder.DisplayName)
        };
        foreach (var child in folder.Children)
            item.Items.Add(BuildMicrosoftFolderNode(account, child));
        return item;
    }

    private MicrosoftMailSession GetMicrosoftSession(ConnectedAccount account)
    {
        if (!_microsoftSessions.TryGetValue(account.AccountId, out var session))
            _microsoftSessions[account.AccountId] = session = new MicrosoftMailSession(account, _secrets, _tokenHttp);
        return session;
    }

    private async void RefreshInboxClicked(object? sender, RoutedEventArgs e)
    {
        if (_activeMicrosoftFolder is not { } folder) return;
        _onlineCancellation?.Cancel();
        _onlineCancellation = new CancellationTokenSource();
        var version = Interlocked.Increment(ref _folderVersion);
        Interlocked.Increment(ref _messageVersion);
        _currentGraphMessages = null;
        MessageList.ItemsSource = null;
        ClearReader();
        await LoadMicrosoftFolderAsync(folder, version, _onlineCancellation.Token);
    }

    private async Task LoadMicrosoftFolderAsync(MicrosoftFolderSelection selection, long version,
        CancellationToken cancellationToken)
    {
        var account = selection.Account;
        StatusText.Text = $"Loading {selection.Name} read-only…";
        try
        {
            var token = await GetMicrosoftSession(account).GetAccessTokenAsync(cancellationToken);
            var reader = new GraphInboxReader(_graphHttp, account.AccountId);
            var page = selection.Id == "inbox"
                ? await reader.GetInboxAsync(token, cancellationToken)
                : await reader.GetFolderAsync(token, selection.Id, cancellationToken);
            if (version != _folderVersion || cancellationToken.IsCancellationRequested) return;
            _currentGraphMessages = page.Messages;
            ShowGraphMessages(page.Messages);
            StatusText.Text = $"{page.Messages.Count} newest of {page.TotalCount:N0} {page.FolderName} items · " +
                $"{page.UnreadCount:N0} unread · {account.DisplayAddress} · read-only";
        }
        catch (OperationCanceledException) { }
        catch (GraphMailException error)
        {
            if (version == _folderVersion)
                StatusText.Text = $"Could not load {selection.Name}: {error.Message}";
        }
        catch (Exception)
        {
            if (version == _folderVersion)
                StatusText.Text = $"Could not load {selection.Name}. Check the connection or use Account setup to sign in again.";
        }
    }

    private void ShowGraphMessages(IReadOnlyList<GraphInboxMessage> messages)
    {
        var rows = messages.Select(message => new GraphMessageListRow(message)).ToArray();
        var view = new DataGridCollectionView(rows);
        if (GroupByDateCheck.IsChecked == true)
            view.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(GraphMessageListRow.DateGroup)));
        _updatingMessageList = true;
        try { MessageList.ItemsSource = view; MessageList.SelectedItem = null; }
        finally { _updatingMessageList = false; }
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
        _folderCache.TryGetValue((path, folder.Nid), out var source);
        var version = Interlocked.Increment(ref _folderVersion);
        Interlocked.Increment(ref _messageVersion);
        _currentMessages = null;
        MessageList.ItemsSource = null;
        ClearReader();
        StatusText.Text = "Searching subject, sender and recipient in selected folder…";
        try
        {
            await _readerGate.WaitAsync(ct);
            IReadOnlyList<MailSummary> results;
            try
            {
                if (!_stores.TryGetValue(path, out var current) || !ReferenceEquals(store, current)) return;
                results = await Task.Run(() => (source ?? store.GetMessages(folder))
                    .Where(m => m.Subject.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                m.From.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                m.To.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .Take(500).ToArray(), ct);
            }
            finally { _readerGate.Release(); }
            if (ct.IsCancellationRequested || version != _folderVersion) return;
            ShowMessages(results);
            StatusText.Text = $"{results.Count} header matches (max 500) · {store.DisplayName} · read-only";
        }
        catch (OperationCanceledException) { /* New selection superseded this search. */ }
        catch (Exception ex) { if (version == _folderVersion) StatusText.Text = $"Search failed: {ex.Message}"; }
    }

    private void ShowMessages(IReadOnlyList<MailSummary> messages)
    {
        _currentMessages = messages;
        var rows = messages.Select(message => new MessageListRow(message)).ToArray();
        var view = new DataGridCollectionView(rows);
        if (GroupByDateCheck.IsChecked == true)
            view.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(MessageListRow.DateGroup)));
        _updatingMessageList = true;
        try
        {
            MessageList.ItemsSource = view;
            MessageList.SelectedItem = null;
        }
        finally { _updatingMessageList = false; }
    }

    private void GroupByDateChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressGroupToggle || (_currentMessages is null && _currentGraphMessages is null)) return;
        var messages = _currentMessages;
        var graphMessages = _currentGraphMessages;
        Interlocked.Increment(ref _messageVersion);
        ClearReader();
        if (messages is not null) ShowMessages(messages);
        else if (graphMessages is not null) ShowGraphMessages(graphMessages);
    }

    private void MessageListSorting(object? sender, DataGridColumnEventArgs e)
    {
        if (GroupByDateCheck.IsChecked != true || MessageList.ItemsSource is not DataGridCollectionView view)
            return;
        // A column click sorts the whole folder; date sections are the default view only.
        Interlocked.Increment(ref _messageVersion);
        ClearReader();
        view.GroupDescriptions.Clear();
        _suppressGroupToggle = true;
        try { GroupByDateCheck.IsChecked = false; }
        finally { _suppressGroupToggle = false; }
    }

    private static void MessageListLoadingRowGroup(object? sender, DataGridRowGroupHeaderEventArgs e)
    {
        e.RowGroupHeader.IsPropertyNameVisible = false;
        e.RowGroupHeader.IsItemCountVisible = false;
    }

    private void CacheFolder((string Path, uint FolderNid) key, IReadOnlyList<MailSummary> messages)
    {
        const int maximumFolders = 8;
        if (_folderCache.ContainsKey(key)) return;
        if (_folderCacheOrder.Count >= maximumFolders)
            _folderCache.Remove(_folderCacheOrder.Dequeue());
        _folderCache[key] = messages;
        _folderCacheOrder.Enqueue(key);
    }

    private async void MessageSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingMessageList) return;
        var version = Interlocked.Increment(ref _messageVersion);
        _activeMessage = null;
        ExportAttachmentButton.IsEnabled = false;
        ExportMessageButton.IsEnabled = false;
        if (MessageList.SelectedItem is GraphMessageListRow { Message: var graphMessage } &&
            _activeMicrosoftAccount is { } account)
        {
            await OpenGraphMessageAsync(account, graphMessage, version);
            return;
        }
        if (MessageList.SelectedItem is not MessageListRow { Summary: var summary } ||
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
            if (version != _messageVersion) return;
            _activeMessage = message;
            ExportAttachmentButton.IsEnabled = message.Attachments.Count > 0;
            ExportMessageButton.IsEnabled = message.Attachments.Count == 0;
            SubjectText.Text = message.Summary.Subject;
            SenderText.Text = $"From: {message.Summary.From}";
            RecipientText.Text = $"To: {message.Summary.To}";
            AttachmentText.Text = message.Attachments.Count == 0 ? "" :
                $"Attachments: {string.Join(", ", message.Attachments.Select(a => a.FileName))}";
            SetMessageBody(message.BodyHtml, message.BodyText);
            StatusText.Text = _richRuns is null ? "Message opened read-only." :
                "Message opened in safe rich-text preview. Images are blocked.";
        }
        catch (Exception ex) { if (version == _messageVersion) StatusText.Text = $"Could not read message: {ex.Message}"; }
    }

    private async Task OpenGraphMessageAsync(ConnectedAccount account, GraphInboxMessage message, long version)
    {
        StatusText.Text = "Reading Microsoft message…";
        try
        {
            var cancellationToken = _onlineCancellation?.Token ?? CancellationToken.None;
            var token = await GetMicrosoftSession(account).GetAccessTokenAsync(cancellationToken);
            var body = await new GraphInboxReader(_graphHttp, account.AccountId)
                .GetMessageBodyAsync(token, message.Id, cancellationToken);
            if (version != _messageVersion || cancellationToken.IsCancellationRequested) return;
            SubjectText.Text = message.Subject;
            SenderText.Text = $"From: {message.From}";
            RecipientText.Text = $"To: {message.To}";
            AttachmentText.Text = message.HasAttachments ? "Attachments present (export is not yet available)." : "";
            SetMessageBody(string.Equals(body?.ContentType, "html", StringComparison.OrdinalIgnoreCase)
                    ? body?.Content : null,
                string.Equals(body?.ContentType, "text", StringComparison.OrdinalIgnoreCase)
                    ? body?.Content : body is null ? message.Preview : "");
            StatusText.Text = _richRuns is null ? "Microsoft message opened read-only." :
                "Microsoft message opened in safe rich-text preview. Images are blocked.";
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        { if (version == _messageVersion) StatusText.Text = "Could not read this message. Try signing in again from Account setup."; }
    }

    private async void ExportMessageClicked(object? sender, RoutedEventArgs e)
    {
        var message = _activeMessage;
        var path = _activePath;
        var version = _messageVersion;
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
        if (version != _messageVersion || !ReferenceEquals(_activeMessage, message)) return;
        var directory = folders.FirstOrDefault()?.TryGetLocalPath();
        if (directory is null) return;
        var output = Path.Combine(directory, $"message-{message.Summary.Nid:X8}.eml");
        ExportMessageButton.IsEnabled = false;
        try
        {
            await _readerGate.WaitAsync();
            try
            {
                if (version != _messageVersion || !ReferenceEquals(_activeMessage, message) ||
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
        var version = _messageVersion;
        if (message is null || path is null || !_stores.TryGetValue(path, out var store) ||
            message.Attachments.Count == 0) return;
        // A separate attachment choice is required for messages with multiple files.
        var attachment = message.Attachments.Count == 1 ? message.Attachments[0] :
            await ChooseAttachmentAsync(message.Attachments);
        if (attachment is null || version != _messageVersion || !ReferenceEquals(_activeMessage, message)) return;
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
        if (version != _messageVersion || !ReferenceEquals(_activeMessage, message)) return;
        var directory = folders.FirstOrDefault()?.TryGetLocalPath();
        if (directory is null) return;
        var output = Path.Combine(directory, suggested);
        ExportAttachmentButton.IsEnabled = false;
        try
        {
            await _readerGate.WaitAsync();
            try
            {
                if (version != _messageVersion || !ReferenceEquals(_activeMessage, message) ||
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
        Interlocked.Increment(ref _folderVersion);
        Interlocked.Increment(ref _messageVersion);
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
            _currentMessages = null;
            MessageList.ItemsSource = null;
            foreach (var key in _folderCache.Keys.Where(key => key.Path == path).ToArray())
                _folderCache.Remove(key);
            var remaining = _folderCacheOrder.Where(key => key.Path != path).ToArray();
            _folderCacheOrder.Clear();
            foreach (var key in remaining) _folderCacheOrder.Enqueue(key);
            ClearReader();
            StatusText.Text = $"Detached {Path.GetFileName(path)}; archive file was not deleted.";
        }
        finally { _readerGate.Release(); }
    }

    private async void OptionsClicked(object? sender, RoutedEventArgs e)
    {
        var dialog = new Window
        {
            Title = "Appearance options", Width = 390, MinWidth = 330,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            FontSize = _appearance.TextSize
        };
        var theme = new ComboBox { ItemsSource = new[] { "Light", "Dark" }, SelectedIndex = _appearance.DarkMode ? 1 : 0 };
        var accent = new ComboBox { ItemsSource = new[] { "Blue", "Green", "Purple", "Orange" },
            SelectedItem = _appearance.Accent };
        var size = new Slider { Minimum = 12, Maximum = 23, TickFrequency = 1,
            IsSnapToTickEnabled = true, Value = _appearance.TextSize, Width = 210 };
        var sizeLabel = new TextBlock { Text = _appearance.TextSize.ToString(), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        size.PropertyChanged += (_, args) =>
        {
            if (args.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty)
                sizeLabel.Text = ((int)Math.Round(size.Value)).ToString();
        };
        var save = new Button { Content = "Save", MinWidth = 76 };
        var cancel = new Button { Content = "Cancel", MinWidth = 76 };
        save.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { cancel, save } };
        var sizeRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
            Children = { size, sizeLabel } };
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Theme" }, theme,
                new TextBlock { Text = "Accent color", Margin = new Thickness(0, 6, 0, 0) }, accent,
                new TextBlock { Text = "Text size", Margin = new Thickness(0, 6, 0, 0) }, sizeRow,
                buttons
            }
        };
        if (await dialog.ShowDialog<bool>(this) != true) return;
        _appearance = AppearanceSettingsStore.Validate(new AppearanceSettings
        {
            DarkMode = theme.SelectedIndex == 1,
            Accent = accent.SelectedItem as string ?? "Blue",
            TextSize = (int)Math.Round(size.Value)
        });
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
        FontSize = size;
        AppTitleText.FontSize = size + 7;
        FolderTree.FontSize = MessageList.FontSize = BodyText.FontSize = size;
        SubjectText.FontSize = size + 5;
        SenderText.FontSize = RecipientText.FontSize = AttachmentText.FontSize = size;
        BodyViewButton.FontSize = size;
        StatusText.FontSize = size - 1;
        OpenPstButton.FontSize = DetachButton.FontSize = OptionsButton.FontSize = size;
        ArchiveSearchButton.FontSize = ExportAttachmentButton.FontSize = ExportMessageButton.FontSize = PreviewJunkImportButton.FontSize = AccountSetupButton.FontSize = RefreshInboxButton.FontSize = ArchiveSearchBox.FontSize = size;
        if (_richRuns is not null) ShowMessageBody();
    }

    private void ApplyViewLayout(ViewLayoutSettings settings)
    {
        PaneGrid.ColumnDefinitions[0].Width = new GridLength(settings.FolderPaneWeight, GridUnitType.Star);
        PaneGrid.ColumnDefinitions[2].Width = new GridLength(settings.MessagePaneWeight, GridUnitType.Star);
        PaneGrid.ColumnDefinitions[4].Width = new GridLength(settings.ReaderPaneWeight, GridUnitType.Star);
        foreach (var column in settings.Columns.OrderBy(column => column.DisplayIndex))
        {
            var target = MessageList.Columns[column.ColumnIndex];
            target.DisplayIndex = column.DisplayIndex;
            target.Width = new DataGridLength(column.Width,
                column.IsStar ? DataGridLengthUnitType.Star : DataGridLengthUnitType.Pixel);
        }
    }

    private void PersistViewLayout()
    {
        var folder = PaneGrid.ColumnDefinitions[0].ActualWidth;
        var messages = PaneGrid.ColumnDefinitions[2].ActualWidth;
        var reader = PaneGrid.ColumnDefinitions[4].ActualWidth;
        var total = folder + messages + reader;
        if (total <= 0) return;
        var columns = MessageList.Columns.Select((column, index) =>
        {
            var isStar = column.Width.UnitType == DataGridLengthUnitType.Star;
            var width = column.Width.UnitType is DataGridLengthUnitType.Pixel or DataGridLengthUnitType.Star
                ? column.Width.Value : column.ActualWidth;
            return new MessageColumnLayout(index, column.DisplayIndex, width, isStar);
        }).ToArray();
        var settings = new ViewLayoutSettings
        {
            FolderPaneWeight = 11 * folder / total,
            MessagePaneWeight = 11 * messages / total,
            ReaderPaneWeight = 11 * reader / total,
            Columns = columns
        };
        try { _viewLayoutStore.Save(settings); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { /* Layout persistence must not block window close. */ }
    }

    private void PersistAppearance()
    {
        try { _appearanceStore.Save(_appearance); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { StatusText.Text = "Could not save appearance settings; this session's choice still applies."; }
    }

    private void SetMessageBody(string? html, string? plain)
    {
        _richRuns = null;
        _bodyPlain = plain ?? "";
        _showRichBody = true;
        if (!string.IsNullOrWhiteSpace(html))
        {
            try
            {
                _richRuns = SafeHtmlPreview.Parse(html);
                if (string.IsNullOrWhiteSpace(_bodyPlain))
                    _bodyPlain = string.Concat(_richRuns.Select(run => run.Text));
            }
            catch (InvalidDataException)
            {
                if (string.IsNullOrWhiteSpace(_bodyPlain))
                    _bodyPlain = "(This HTML message is too large to preview.)";
            }
        }
        if (string.IsNullOrWhiteSpace(_bodyPlain) && _richRuns is null)
            _bodyPlain = "(No message body available.)";
        ShowMessageBody();
    }

    private void ToggleBodyViewClicked(object? sender, RoutedEventArgs e)
    {
        if (_richRuns is null) return;
        _showRichBody = !_showRichBody;
        ShowMessageBody();
    }

    private void ShowMessageBody()
    {
        BodyText.Inlines?.Clear();
        BodyViewButton.IsVisible = _richRuns is not null;
        BodyViewButton.Content = _showRichBody ? "View plain text" : "View rich text";
        if (!_showRichBody || _richRuns is null)
        {
            BodyText.Text = _bodyPlain;
            return;
        }
        BodyText.Text = "";
        foreach (var segment in _richRuns)
        {
            var run = new Run(segment.Text);
            if (segment.Bold) run.FontWeight = FontWeight.Bold;
            if (segment.Italic) run.FontStyle = FontStyle.Italic;
            if (segment.Underline) run.TextDecorations = TextDecorations.Underline;
            if (segment.Scale != 1) run.FontSize = _appearance.TextSize * segment.Scale;
            BodyText.Inlines?.Add(run);
        }
    }

    private void ClearReader()
    {
        _activeMessage = null;
        ExportAttachmentButton.IsEnabled = false;
        ExportMessageButton.IsEnabled = false;
        _richRuns = null;
        _bodyPlain = "";
        BodyViewButton.IsVisible = false;
        BodyText.Inlines?.Clear();
        SubjectText.Text = "Select a message";
        SenderText.Text = RecipientText.Text = AttachmentText.Text = BodyText.Text = "";
    }

    private sealed record FolderSelection(string Path, MailFolder Folder);
    private sealed record MicrosoftFolderSelection(ConnectedAccount Account, string Id, string Name);
}
