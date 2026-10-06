using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Controls.Primitives;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Input;
using Avalonia.VisualTree;
using System.Diagnostics;
using System.Collections.ObjectModel;
using OpenOutlook.Auth;
using OpenOutlook.JunkCleaner;
using OpenOutlook.Providers.Microsoft;
using OpenOutlook.PstNative;
using PstCore;

namespace OpenOutlook.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly Dictionary<string, IPstEngine> _stores = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Path, uint FolderNid), IReadOnlyList<MailSummary>> _folderCache = new();
    private readonly Queue<(string Path, uint FolderNid)> _folderCacheOrder = new();
    private readonly SemaphoreSlim _readerGate = new(1, 1);
    internal IReadOnlyList<RibbonResponsiveLayout> RibbonLayouts { get; private set; } = [];
    private readonly ConnectedAccountRegistry _accountRegistry = new();
    private readonly ISecretStore _secrets = SecretStores.CreateDefault();
    private readonly HttpClient _tokenHttp = DesktopOAuth.CreateHttpClient();
    private readonly HttpClient _graphHttp = GraphInboxReader.CreateSecureHttpClient();
    private readonly Dictionary<string, MicrosoftMailSession> _microsoftSessions = new(StringComparer.Ordinal);
    private CancellationTokenSource? _onlineCancellation;
    private CancellationTokenSource? _folderRefreshCancellation;
    private CancellationTokenSource? _folderDiscoveryCancellation;
    private ConnectedAccount? _activeMicrosoftAccount;
    private MicrosoftFolderSelection? _activeMicrosoftFolder;
    private IReadOnlyList<GraphInboxMessage>? _currentGraphMessages;
    private ObservableCollection<GraphMessageListRow>? _graphRows;
    private GraphInboxMessage? _activeGraphMessage;
    private IReadOnlyList<GraphAttachment>? _currentGraphAttachments;
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
    private bool _showOriginalHtml;
    private IReadOnlyList<HtmlPreviewRun>? _richRuns;
    private string? _bodyHtml;
    private IReadOnlyList<HtmlImageSource> _htmlImageSources = [];
    private int _failedMessageImages;
    private readonly Dictionary<string, byte[]> _inlineImageBytes = new(StringComparer.Ordinal);
    private readonly List<Bitmap> _htmlPageBitmaps = [];
    private bool _embeddedHtmlActive;
    private bool _preferSnapshotForMessage;
    private TaskCompletionSource<bool>? _embeddedNavigation;
    private const int EmbeddedHtmlMaximumCharacters = 8 * 1024 * 1024;
    private IReadOnlyList<byte[]> _htmlPagePngs = [];
    private double _htmlZoom = 1;
    private CancellationTokenSource? _htmlRenderCancellation;
    private CancellationTokenSource? _inlineImageLoadCancellation;
    private Task? _imageLoadTask;
    private readonly Dictionary<string, Bitmap> _inlineImages = new(StringComparer.Ordinal);
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _folderExportCancellation;
    private readonly AppearanceSettingsStore _appearanceStore = new();
    private readonly ShortcutSettingsStore _shortcutStore = new();
    private readonly ViewLayoutSettingsStore _viewLayoutStore = new();
    private readonly AttachedPstSettingsStore _attachedPstStore = new();
    private readonly DispatcherTimer _layoutSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private bool _layoutReady;
    private double _normalWindowWidth = 1380;
    private double _normalWindowHeight = 850;
    private PixelPoint? _normalWindowPosition;
    private bool _lastWindowMaximized;
    private AppearanceSettings _appearance = new();
    private ShortcutSettings _shortcuts = new();
    private bool _mailActionBusy;
    private Task _messageSelectionTask = Task.CompletedTask;
    private readonly List<NativeWebDialog> _interactiveDialogs = [];

    public MainWindow()
    {
        InitializeComponent();
        RibbonLayouts = RibbonResponsiveLayout.AttachAll(RibbonTabs);                // groups collapse into drop-down buttons as the window narrows
        RemoveWebViewIfUnsupported();
        LoadOAuthClientConfigurationAtStartup();
        _appearance = _appearanceStore.Load();
        _shortcuts = _shortcutStore.Load();
        ApplyAppearance();
        var layout = _viewLayoutStore.Load();
        ApplyViewLayout(layout);
        InitializeFolderOrder();
        InitializeReadingState();
        InitializeReadingOptions();
        _layoutSaveTimer.Tick += (_, _) =>
        {
            _layoutSaveTimer.Stop();
            PersistViewLayout();
        };
        SizeChanged += (_, _) => TrackWindowGeometry();
        PositionChanged += (_, _) => TrackWindowGeometry();
        PropertyChanged += (_, args) =>
        {
            if (args.Property == WindowStateProperty) TrackWindowGeometry();
        };
        foreach (var definition in PaneGrid.ColumnDefinitions)
            definition.PropertyChanged += (_, args) =>
            {
                if (args.Property == ColumnDefinition.WidthProperty) ScheduleLayoutSave();
            };
        PaneGrid.AddHandler(Avalonia.Input.InputElement.PointerReleasedEvent,
            (_, _) => ScheduleLayoutSave(), RoutingStrategies.Bubble, handledEventsToo: true);
        ReaderPane.SizeChanged += (_, _) => FitHtmlPageImage();
        MainHtmlWebView.NavigationStarted += (_, args) =>
        {
            if (SafeHtmlDocument.TryLink(args.Request?.AbsoluteUri, out var url))
            {
                args.Cancel = true;
                OpenReaderLink(url);
            }
            else if (args.Request is { Scheme: not "about" }) args.Cancel = true;
        };
        MainHtmlWebView.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            if (SafeHtmlDocument.TryLink(args.Request?.AbsoluteUri, out var url)) OpenReaderLink(url);
        };
        MainHtmlWebView.NavigationCompleted += (_, _) =>
        {
            ApplyEmbeddedZoom();
            _embeddedNavigation?.TrySetResult(true);
        };
        GroupByDateCheck.IsCheckedChanged += GroupByDateChanged;
        // The status-bar zoom control and the reading pane's own zoom buttons drive one value.
        ZoomSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            _htmlZoom = Math.Clamp(ZoomSlider.Value / 100, 0.5, 2.5);
            FitHtmlPageImage();
            ApplyEmbeddedZoom();
        };
        MessageList.DoubleTapped += MessageListDoubleTapped;
        MessageList.AddHandler(InputElement.KeyDownEvent, MessageListShortcutKeyDown,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        Closing += (_, _) =>
        {
            _layoutSaveTimer.Stop();
            PersistViewLayout();
        };
        Closed += (_, _) =>
        {
            _searchCancellation?.Cancel();
            _onlineCancellation?.Cancel();
            _folderRefreshCancellation?.Cancel();
            _folderDiscoveryCancellation?.Cancel();
            _folderExportCancellation?.Cancel();
            _tokenHttp.Dispose();
            _graphHttp.Dispose();
            foreach (var dialog in _interactiveDialogs.ToArray()) dialog.Close();
            ClearInlineImages();
            // Pending reader tasks hold this gate; close is cooperative until they finish.
            _ = CloseStoresAsync();
        };
        Opened += async (_, _) =>
        {
            RestoreWindowPlacement(layout);
            RefreshConnectedAccounts();
            IReadOnlyList<string> savedArchives;
            try { savedArchives = _attachedPstStore.Load(); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                savedArchives = [];
                StatusText.Text = "Could not read saved PST attachments; check attached-psts.json in your OpenOutlook settings.";
            }
            foreach (var path in savedArchives)
                await OpenArchiveAsync(path, remember: false);
            // Explicit command-line archives enable repeatable headless UI smoke tests.
            foreach (var path in Environment.GetCommandLineArgs().Skip(1).Where(File.Exists))
                await OpenArchiveAsync(Path.GetFullPath(path));
            _ = Task.Run(() =>
            {
                var n = BrowserProcessTracker.KillLeftovers();
                if (n > 0) AppLog.Note("reader", $"closed {n} layout browser(s) left behind by an earlier run");
                var folders = BrowserProcessTracker.DeleteStaleProfiles();
                if (folders > 0) AppLog.Note("reader", $"removed {folders} browser profile folder(s) left behind by an earlier run");
            });
            if (IsElevated()) WarnAboutElevation();
            if (WslDriveBookmarks.IsWsl()) _ = Task.Run(() => WslDriveBookmarks.Ensure());          // the Windows drives in the file dialog's sidebar
            StartMirrorScheduler();                                  // the local copies of connected mailboxes
            _ = WarmUpMicrosoftAsync();                             // token and connection ready before the first click
            StartJunkScheduler();                                   // silent Junk Cleaner runs
            if (Environment.GetEnvironmentVariable("OPENOUTLOOK_SELFTEST_HTML") == "1") _ = RunHtmlSelfTestAsync();      // diagnostics: renders a sample message and logs the outcome
            var unavailable = savedArchives.Count(path => !_stores.ContainsKey(path));
            if (unavailable > 0)
                StatusText.Text = $"{unavailable} saved PST archive{(unavailable == 1 ? "" : "s")} could not be opened; the paths remain saved for the next restart.";
        };
    }

    /// <summary>Opt-in diagnostic (OPENOUTLOOK_SELFTEST_HTML=1): shows a sample HTML message through the normal reading-pane path and writes what happened to the log.</summary>
    private async Task RunHtmlSelfTestAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("OPENOUTLOOK_SELFTEST_DELAY"), out var wait) ? wait : 8));
        var html = Environment.GetEnvironmentVariable("OPENOUTLOOK_SELFTEST_FILE") is { Length: > 0 } file && File.Exists(file) ? File.ReadAllText(file) : "<html><body><table width=\"600\"><tr><td style=\"background:#123\"><h1 style=\"color:#fff\">Self test</h1></td></tr><tr><td><p>Hello from the reader self test.</p><img src=\"https://example.org/x.png\" alt=\"x\"></td></tr></table></body></html>";
        var started = DateTime.Now;
        SetMessageBody(html, "");
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(1000);
            if (_embeddedHtmlActive || HtmlPagesPanel.Children.Count > 0) break;
        }
        await Task.Delay(TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("OPENOUTLOOK_SELFTEST_SETTLE"), out var settle) ? settle : 0));     // remote images arrive after the first layout
        AppLog.Note("selftest", $"after {(DateTime.Now - started).TotalSeconds:0.0}s: embedded={_embeddedHtmlActive}, snapshot pages={HtmlPagesPanel.Children.Count}, status='{StatusText.Text}', html status='{HtmlStatusText.Text}'");
        if (Environment.GetEnvironmentVariable("OPENOUTLOOK_SELFTEST_SHOT") is { Length: > 0 } shot)
        {
            try
            {
                var size = new Avalonia.PixelSize(Math.Max(100, (int)ReaderPane.Bounds.Width), Math.Max(100, (int)ReaderPane.Bounds.Height));
                using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
                bitmap.Render(ReaderPane);
                bitmap.Save(shot);
                AppLog.Note("selftest", "reading pane saved to " + shot);
            }
            catch (Exception ex) { AppLog.Error("selftest", ex, "could not save the reading pane picture"); }
        }
    }

    private void RibbonPlaceholderClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string command })
            StatusText.Text = $"{command} is planned and not available yet.";
    }

    private async void MailActionClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string action }) return;
        await ExecuteMailActionAsync(action);
    }

    private async Task ExecuteMailActionAsync(string action)
    {
        if (await TryHandlePstFlagActionAsync(action)) return;
        if (await TryHandlePstDeleteAsync(action)) return;
        if (await TryHandleGmailActionAsync(action)) return;
        if (action == "new") { OpenCompose(_activeMicrosoftAccount); return; }
        ConnectedAccount? account = _activeMicrosoftAccount;
        if (account is null)
        {
            await ExplainMailActionAsync("Select a message in a connected Microsoft mailbox to use this action. For PST archives, right-click the archive and turn on Editing Mode.", null);
            return;
        }
        if (!account.CanWriteMicrosoftMail || !account.CanSendMicrosoftMail)
        {
            await ExplainMailActionAsync("This saved Microsoft sign-in has read-only mail access. Sign in again to allow composing, replying, forwarding and organizing mail.", account);
            return;
        }
        var writer = new GraphMailWriter(_graphHttp, account.AccountId);
        Task<string> Token() => GetMicrosoftSession(account).GetAccessTokenAsync();
        if (MessageList.SelectedItem is not GraphMessageListRow { Message: var selected })
        {
            StatusText.Text = "Select a Microsoft message first.";
            return;
        }
        if (_mailActionBusy) return;
        if (action == "editDraft")
        {
            if (!selected.IsDraft) { StatusText.Text = "Select a message in Drafts to edit it."; return; }
            OpenCompose(account, null, selected.Id);
            return;
        }
        var actionFolder = _activeMicrosoftFolder;
        var selectedForDelete = action == "delete"
            ? MessageList.SelectedItems.OfType<GraphMessageListRow>()
                .Select(row => row.Message).DistinctBy(message => message.Id).ToArray()
            : [];
        if (actionFolder is not null && _activeMicrosoftFolder == actionFolder && CanActAtOnce(action, actionFolder))
        {
            ActAtOnce(account, actionFolder, action, action == "delete" && selectedForDelete.Length > 0 ? selectedForDelete : [selected]);
            return;
        }
        _mailActionBusy = true;
        try
        {
            if (action is "reply" or "replyAll" or "forward")
            {
                await CreateResponseDraftAsync(account, selected.Id, action, this);
                return;
            }
            if (action == "delete")
            {
                await DeleteSelectedMessagesAsync(writer, Token, selectedForDelete.Length > 0
                    ? selectedForDelete : [selected], actionFolder);
                return;
            }
            switch (action)
            {
                case "archive": await writer.MoveAsync(await Token(), selected.Id, "archive"); break;
                case "read": await writer.SetReadAsync(await Token(), selected.Id, true); break;
                case "unread": await writer.SetReadAsync(await Token(), selected.Id, false); break;
                case "flag": await writer.SetFlagAsync(await Token(), selected.Id, true); break;
                case "unflag": await writer.SetFlagAsync(await Token(), selected.Id, false); break;
                default: return;
            }
            if (actionFolder is not null && _activeMicrosoftFolder == actionFolder)
            {
                ApplyCompletedMailAction(selected, action);
                StatusText.Text = "Mail action completed. Updating this folder…";
                await RefreshMicrosoftFolderAsync();
            }
            else StatusText.Text = "Mail action completed.";
        }
        catch (GraphMailException error) when (error.StatusCode == System.Net.HttpStatusCode.Forbidden ||
            error.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        { StatusText.Text = "Microsoft declined mail access. Use Account setup to sign in again, then retry."; }
        catch (Exception error)
        { StatusText.Text = error is GraphMailException or ArgumentException ? error.Message :
            "Mail action failed. Check the connection and retry."; }
        finally { _mailActionBusy = false; }
    }

    private async Task DeleteSelectedMessagesAsync(GraphMailWriter writer, Func<Task<string>> token,
        IReadOnlyList<GraphInboxMessage> messages, MicrosoftFolderSelection? actionFolder)
    {
        StatusText.Text = $"Checking {messages.Count} selected message(s)…";
        var result = await GraphMailBatchDeleter.DeleteAsync(writer, await token(), messages,
            ConfirmPermanentDeleteAsync,
            (done, total) => StatusText.Text = $"Deleting {done} of {total} messages…");
        if (result.Canceled) { StatusText.Text = "Deletion canceled."; return; }
        if (result.Completed.Count > 0 && actionFolder is not null && _activeMicrosoftFolder == actionFolder)
        {
            foreach (var message in result.Completed) ApplyCompletedMailAction(message, "delete");
            await RefreshMicrosoftFolderAsync();
        }
        if (result.Failure is null)
            StatusText.Text = result.Completed.Count == 1 ? "Message deleted." :
                $"{result.Completed.Count} messages deleted.";
        else
            StatusText.Text = $"Deleted {result.Completed.Count} of {result.Requested} messages. The remaining messages were not changed: " +
                (result.Failure is GraphMailException or ArgumentException ? result.Failure.Message : "check the connection and retry.");
    }

    private async Task CreateResponseDraftAsync(ConnectedAccount account, string messageId,
        string action, Window owner)
    {
        StatusText.Text = "Creating a response draft in your Microsoft mailbox…";
        var writer = new GraphMailWriter(_graphHttp, account.AccountId);
        Task<string> Token() => GetMicrosoftSession(account).GetAccessTokenAsync();
        var draftId = await writer.CreateResponseDraftAsync(await Token(), messageId, action);
        OpenCompose(account, null, draftId);
        StatusText.Text = "Draft created. Close the compose window to keep it in Drafts.";
    }

    private void ApplyCompletedMailAction(GraphInboxMessage selected, string action)
    {
        if (_graphRows is null) return;
        var row = _graphRows.FirstOrDefault(item => item.Message.Id == selected.Id);
        if (row is null) return;
        if (_activeGmailFolder is { } gmailNow)
        {
            var delta = action switch { "read" when !row.Message.IsRead => -1, "unread" when row.Message.IsRead => 1, "delete" or "archive" when !row.Message.IsRead => -1, _ => 0 };
            if (delta != 0) AdjustGmailUnread(gmailNow, delta);
        }
        if ((_activeGmailFolder?.Account ?? _activeMicrosoftAccount) is { } changedAccount) NoteLocalChange(changedAccount, selected.Id, action);
        if (action is "delete" or "archive")
        {
            if (ReferenceEquals(MessageList.SelectedItem, row))
            {
                MessageList.SelectedItem = null;
                Interlocked.Increment(ref _messageVersion);
                ClearReader();
            }
            _graphRows.Remove(row);
        }
        else
        {
            var updated = action switch
            {
                "read" => row.Message with { IsRead = true },
                "unread" => row.Message with { IsRead = false },
                "flag" => row.Message with { IsFlagged = true },
                "unflag" => row.Message with { IsFlagged = false },
                _ => row.Message
            };
            row.Update(updated);
            if (_activeGraphMessage?.Id == updated.Id) _activeGraphMessage = updated;
            // Keep the local read overlay in step with an explicit mark-read/unread so a later
            // refresh cannot resurrect a stale auto-marked state.
            if (action is "read" or "unread" && _activeMicrosoftAccount is { } accountForState)
            {
                _readOverrides[GraphMessageKey(accountForState.AccountId, updated.Id)] = action == "read";
                PersistReadState();
            }
        }
        _currentGraphMessages = _graphRows.Select(item => item.Message).ToArray();
    }

    private async void RespondFromMessageWindow(ConnectedAccount account, string messageId,
        string action, Window owner)
    {
        if (_mailActionBusy) return;
        try
        {
            account = _accountRegistry.Load().FirstOrDefault(saved =>
                saved.Provider == account.Provider && saved.AccountId == account.AccountId) ?? account;
        }
        catch (Exception) { /* The response request below reports unavailable account settings. */ }
        if (!account.CanWriteMicrosoftMail || !account.CanSendMicrosoftMail)
        {
            await ExplainMailActionAsync("This saved Microsoft sign-in has read-only mail access. Sign in again to reply or forward.", account);
            return;
        }
        _mailActionBusy = true;
        try { await CreateResponseDraftAsync(account, messageId, action, owner); }
        catch (Exception error)
        { StatusText.Text = error is GraphMailException or ArgumentException ? error.Message :
            "Could not create the response draft. Check the connection and retry."; }
        finally { _mailActionBusy = false; }
    }

    private async Task ExplainMailActionAsync(string explanation, ConnectedAccount? reconnect)
    {
        StatusText.Text = explanation;
        var dialog = new Window { Title = "Mail action — OpenOutlook", Width = 470,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var setup = new Button { Content = reconnect is null ? "Account setup" : "Sign in again" };
        var close = new Button { Content = "Close" };
        setup.Click += (_, _) => dialog.Close(true);
        close.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel { Margin = new Thickness(18), Spacing = 14, Children =
        {
            new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap },
            new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
                Children = { setup, close } }
        } };
        if (await dialog.ShowDialog<bool>(this) != true) return;
        await new AccountSetupWindow(reconnect).ShowDialog(this);
        RefreshConnectedAccounts();
        StatusText.Text = "Account settings updated. Select a Microsoft folder, then try the mail action again.";
    }

    private async Task<bool> ConfirmPermanentDeleteAsync(int count)
    {
        var dialog = new Window { Title = count == 1 ? "Permanently delete message?" : "Permanently delete messages?", Width = 440, Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var delete = new Button { Content = "Delete permanently" };
        var cancel = new Button { Content = "Cancel" };
        delete.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel { Margin = new Thickness(18), Spacing = 14, Children =
        {
            new TextBlock { Text = count == 1
                ? "This message is already in Deleted Items. Permanently delete it?"
                : $"{count} selected messages are already in Deleted Items. Permanently delete them?",
                TextWrapping = TextWrapping.Wrap },
            new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
                Children = { delete, cancel } }
        } };
        return await dialog.ShowDialog<bool>(this);
    }

    private async void OpenPstClicked(object? sender, RoutedEventArgs e)
    {
        var chosen = await SafePick.FilesAsync(this, new FilePickerOpenOptions
        {
            Title = "Open a PST archive read-only",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Outlook PST") { Patterns = ["*.pst"] }]
        }, failure => StatusText.Text = failure);
        foreach (var file in chosen)
        {
            var path = file.TryGetLocalPath();
            if (path is not null) await OpenArchiveAsync(Path.GetFullPath(path));
        }
    }

    private async Task<bool> OpenArchiveAsync(string path, bool remember = true)
    {
        if (_stores.ContainsKey(path)) return true;
        StatusText.Text = $"Opening {Path.GetFileName(path)}…";
        try
        {
            // Serialize reader use: NDB protects individual reads, not whole operations.
            await _readerGate.WaitAsync();
            IPstEngine store;
            try
            {
                string? readOnlyReason = null;
                store = await Task.Run(() =>
                {
                    var opened = PstEngineFactory.OpenEditable(path, out readOnlyReason);
                    if (opened.CanWrite) BackupBeforeEditingIfRequested(path);
                    return opened;
                });
                if (readOnlyReason is not null) _readOnlyReasons[path] = readOnlyReason; else _readOnlyReasons.Remove(path);
                AppLog.Note("pst-engine", $"{Path.GetFileName(path)} opened, {(store.CanWrite ? "editable" : "read-only")}" + (readOnlyReason is null ? "" : $" ({readOnlyReason})"));
                if (store is NativePstEngine native)
                {
                    native.BackgroundWriteFailed += ex =>
                    {
                        AppLog.Error("pst-edit", ex, "background write of read/flag changes failed");
                        Dispatcher.UIThread.Post(() => StatusText.Text = $"Changes to {Path.GetFileName(path)} could not be saved: {ex.Message}");
                    };
                }
                if (store is NativePstEngine { RecoveredFromInterruptedWrite: true })
                    AppLog.Note("pst-engine", $"{Path.GetFileName(path)}: an interrupted write was rolled back from the journal");
            }
            finally { _readerGate.Release(); }
            if (_stores.TryAdd(path, store))
            {
                FolderTree.Items.Add(BuildArchiveNode(path, store));
                ApplyFolderOrder(FolderTree);
                StatusText.Text = store.CanWrite
                    ? $"Opened {store.DisplayName}."
                    : $"Opened {store.DisplayName} read-only: {(_readOnlyReasons.TryGetValue(path, out var why) ? why : "not editable")}";
                if (remember)
                {
                    try { _attachedPstStore.Add(path); }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                    { StatusText.Text = $"Opened {store.DisplayName}, but could not save it for the next restart."; }
                }
            }
            else store.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not open {Path.GetFileName(path)}: {ex.Message}";
            return false;
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

    private TreeViewItem BuildArchiveNode(string path, IPstEngine store)
    {
        var roots = PstFolderPresentation.VisibleRoots(store.Root);
        var countVisited = new HashSet<uint>();
        var unread = roots.Sum(folder => CountUnread(folder, countVisited));
        var root = new TreeViewItem { Header = FolderHeader(store.DisplayName, unread), Tag = path, IsExpanded = true };
        EnableFolderReordering(root);
        var visited = new HashSet<uint>();
        foreach (var folder in roots)
            AddChildren(root, folder, path, visited);
        ApplyFolderOrder(root);
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

    private void AddChildren(ItemsControl parent, MailFolder folder, string path, HashSet<uint> visited)
    {
        if (!visited.Add(folder.Nid)) return;
        var item = new TreeViewItem { Header = FolderHeader(folder.Name, folder.UnreadCount), Tag = new FolderSelection(path, folder), IsExpanded = folder.ParentNid == 0 };
        EnableFolderReordering(item);
        EnableMoveDrops(item);
        parent.Items.Add(item);
        foreach (var child in folder.Children) AddChildren(item, child, path, visited);
        ApplyFolderOrder(item);
    }

    private async void FolderSelected(object? sender, SelectionChangedEventArgs e)
    {
        var version = Interlocked.Increment(ref _folderVersion);
        Interlocked.Increment(ref _messageVersion);
        _searchCancellation?.Cancel();
        _onlineCancellation?.Cancel();
        _folderRefreshCancellation?.Cancel();
        _activeMicrosoftAccount = null;
        _activeMicrosoftFolder = null;
        _activeGmailFolder = null;
        _currentGraphMessages = null;
        _graphRows = null;
        RefreshInboxButton.IsEnabled = false;
        ExportFolderButton.IsEnabled = false;
        if (FolderTree.SelectedItem is TreeViewItem { Tag: GmailFolderSelection gmail })
        {
            _activePath = null;
            _activeFolder = null;
            _currentMessages = null;
            MessageList.ItemsSource = null; RefreshItemCount();
            ClearReader();
            _activeGmailFolder = gmail;
            RefreshInboxButton.IsEnabled = true;
            _onlineCancellation = new CancellationTokenSource();
            await LoadGmailFolderAsync(gmail, version, _onlineCancellation.Token);
            return;
        }
        if (FolderTree.SelectedItem is TreeViewItem { Tag: MicrosoftFolderSelection online })
        {
            _activePath = null;
            _activeFolder = null;
            _currentMessages = null;
            MessageList.ItemsSource = null; RefreshItemCount();
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
            MessageList.ItemsSource = null; RefreshItemCount();
            ClearReader();
            return;
        }
        if (!_stores.TryGetValue(selection.Path, out var store)) return;
        ExportFolderButton.IsEnabled = _folderExportCancellation is null;
        _activePath = selection.Path;
        _activeFolder = selection.Folder;
        _currentMessages = null;
        MessageList.ItemsSource = null; RefreshItemCount();
        ClearReader();
        var cacheKey = (selection.Path, selection.Folder.Nid);
        if (_folderCache.TryGetValue(cacheKey, out var cached))
        {
            ShowMessages(cached);
            StatusText.Text = $"{cached.Count} messages · {store.DisplayName} · {(store.CanWrite ? "EDITING" : "read-only")}";
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
            StatusText.Text = $"{messages.Count} messages · {store.DisplayName} · {(store.CanWrite ? "EDITING" : "read-only")}";
        }
        catch (Exception ex) { if (version == _folderVersion) StatusText.Text = $"Could not read folder: {ex.Message}"; }
    }

    private async void AccountSetupClicked(object? sender, RoutedEventArgs e) => await ShowAccountSetupAsync();

    /// <summary>Shared by the ribbon button and the Backstage Info page so both refresh their own state after.</summary>
    private async Task ShowAccountSetupAsync()
    {
        await new AccountSetupWindow().ShowDialog(this);
        _activeMicrosoftAccount = null;
        _activeMicrosoftFolder = null;
        _activeGmailFolder = null;
        _gmailBoxes.Clear();
        _currentGraphMessages = null;
        _graphRows = null;
        Interlocked.Increment(ref _messageVersion);
        MessageList.ItemsSource = null; RefreshItemCount();
        ClearReader();
        RefreshConnectedAccounts();
        StatusText.Text = "Account settings updated. Select a mailbox folder to continue.";
    }

    private void RefreshConnectedAccounts()
    {
        _folderDiscoveryCancellation?.Cancel();
        _folderDiscoveryCancellation = new CancellationTokenSource();
        foreach (var existing in FolderTree.Items.OfType<TreeViewItem>()
            .Where(item => item.Tag is ConnectedAccount).ToArray())
            FolderTree.Items.Remove(existing);
        _microsoftSessions.Clear();
        _gmailBoxes.Clear();
        try
        {
            foreach (var account in _accountRegistry.Load().Where(account => account.Provider == OAuthProvider.MicrosoftConsumers))
            {
                var root = new TreeViewItem { Header = account.DisplayAddress, Tag = account, IsExpanded = true };
                EnableFolderReordering(root);
                var inbox = new TreeViewItem { Header = "Inbox", Tag = new MicrosoftFolderSelection(account, "inbox", "Inbox") };
                EnableFolderReordering(inbox);
                root.Items.Add(inbox);
                FolderTree.Items.Add(root);
                _ = LoadAccountFoldersAsync(account, root, _folderDiscoveryCancellation.Token);
            }
            AddGmailAccountNodes();
            ApplyFolderOrder(FolderTree);
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
                        _msUnread[account.AccountId + "|inbox"] = folder.UnreadCount;
                        foreach (var child in folder.Children)
                            inbox.Items.Add(BuildMicrosoftFolderNode(account, child));
                        ApplyFolderOrder(inbox);
                    }
                }
                else root.Items.Add(BuildMicrosoftFolderNode(account, folder));
            }
            ApplyFolderOrder(root);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!cancellationToken.IsCancellationRequested && FolderTree.Items.Contains(root) &&
                _activeMicrosoftAccount is null)
                StatusText.Text = "Could not list all account folders. Inbox is still available.";
        }
    }

    /// <summary>Re-reads the unread counts of an account's folders and redraws the folder list headers (after a delete, move or read change).</summary>
    private async Task RefreshMicrosoftFolderCountsAsync(ConnectedAccount account, CancellationToken cancellationToken, bool force = false)
    {
        if (!force && _countsRefreshed.TryGetValue(account.AccountId, out var last) && DateTime.UtcNow - last < TimeSpan.FromSeconds(60)) return;      // the folder tree is only re-read about once a minute
        _countsRefreshed[account.AccountId] = DateTime.UtcNow;
        try
        {
            var token = await GetMicrosoftSession(account).GetAccessTokenAsync(cancellationToken);
            var folders = await new GraphMailFolderReader(_graphHttp, account.AccountId).GetFoldersAsync(token, cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            void Collect(GraphMailboxFolder f)
            {
                counts[f.Id] = f.UnreadCount;
                if (f.DisplayName.Equals("Inbox", StringComparison.OrdinalIgnoreCase)) counts["inbox"] = f.UnreadCount;
                foreach (var c in f.Children) Collect(c);
            }
            foreach (var f in folders) Collect(f);
            ApplyFolderCounts(account, counts);
        }
        catch (Exception) { }                                                         // the counts are cosmetic; the next refresh tries again
    }

    private readonly Dictionary<string, GraphInboxPage> _graphPageCache = new(StringComparer.Ordinal);      // accountId|folderId -> the last list shown
    private readonly Dictionary<string, DateTime> _countsRefreshed = new(StringComparer.Ordinal);

    /// <summary>Redraws the unread numbers of the given folders (by id) in the folder list.</summary>
    private void ApplyFolderCounts(ConnectedAccount account, IReadOnlyDictionary<string, int> counts)
    {
        void Walk(IEnumerable<object?> items)
        {
            foreach (var node in items.OfType<TreeViewItem>())
            {
                if (node.Tag is MicrosoftFolderSelection sel && sel.Account.AccountId == account.AccountId && counts.TryGetValue(sel.Id, out var n)
                    && (!_msUnread.TryGetValue(account.AccountId + "|" + sel.Id, out var old) || old != n))
                {
                    _msUnread[account.AccountId + "|" + sel.Id] = n;
                    node.Header = FolderHeader(sel.Name, n);
                }
                Walk(node.Items.Cast<object?>());
            }
        }
        Walk(FolderTree.Items.Cast<object?>());
    }

    private TreeViewItem BuildMicrosoftFolderNode(ConnectedAccount account, GraphMailboxFolder folder)
    {
        _msUnread[account.AccountId + "|" + folder.Id] = folder.UnreadCount;
        var item = new TreeViewItem
        {
            Header = FolderHeader(folder.DisplayName, folder.UnreadCount),
            Tag = new MicrosoftFolderSelection(account, folder.Id, folder.DisplayName)
        };
        EnableFolderReordering(item);
        foreach (var child in folder.Children)
            item.Items.Add(BuildMicrosoftFolderNode(account, child));
        ApplyFolderOrder(item);
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
        if (_activeGmailFolder is { } gmail)
        {
            _folderRefreshCancellation?.Cancel();
            _folderRefreshCancellation = new CancellationTokenSource();
            await LoadGmailFolderAsync(gmail, Interlocked.Increment(ref _folderVersion), _folderRefreshCancellation.Token);
            return;
        }
        if (_activeMicrosoftFolder is { } current) await SyncMirrorAsync(current.Account, manual: true);       // the copy is brought up to date first
        await RefreshMicrosoftFolderAsync();
    }

    private async Task RefreshMicrosoftFolderAsync()
    {
        if (_activeMicrosoftFolder is not { } folder) return;
        _folderRefreshCancellation?.Cancel();
        _folderRefreshCancellation = new CancellationTokenSource();
        var version = Interlocked.Increment(ref _folderVersion);
        await LoadMicrosoftFolderAsync(folder, version, _folderRefreshCancellation.Token);
    }

    private async Task LoadMicrosoftFolderAsync(MicrosoftFolderSelection selection, long version,
        CancellationToken cancellationToken)
    {
        var account = selection.Account;
        StatusText.Text = $"Loading {selection.Name}…";
        try
        {
            var cacheKey = account.AccountId + "|" + selection.Id;
            if (await ReadLocalFolderAsync(account, selection.Id) is { } local)             // the local copy answers without a network request
            {
                if (version != _folderVersion || cancellationToken.IsCancellationRequested) return;
                _currentGraphMessages = local.Messages;
                if (_graphRows is null) ShowGraphMessages(local.Messages);
                else ReconcileGraphMessages(local.Messages);
                StatusText.Text = $"{local.Messages.Count} newest of {local.TotalCount:N0} {local.FolderName} items · {local.UnreadCount:N0} unread · {account.DisplayAddress} · local copy";
                ApplyFolderCounts(account, new Dictionary<string, int> { [selection.Id] = local.UnreadCount });
                if (!_mirrorLastSync.TryGetValue(account.AccountId, out var lastSync) || DateTime.UtcNow - lastSync > TimeSpan.FromSeconds(90))
                    RequestMirrorSyncSoon(account);                                          // new mail on the server appears once the copy is refreshed
                return;
            }
            if (_graphRows is null && _graphPageCache.TryGetValue(cacheKey, out var cached))      // a folder seen before shows at once; the fresh list replaces it below
            {
                _currentGraphMessages = cached.Messages;
                ShowGraphMessages(cached.Messages);
                StatusText.Text = $"{cached.Messages.Count} newest of {cached.TotalCount:N0} {cached.FolderName} items · refreshing…";
            }
            var token = await GetMicrosoftSession(account).GetAccessTokenAsync(cancellationToken);
            var reader = new GraphInboxReader(_graphHttp, account.AccountId);
            var page = selection.Id == "inbox"
                ? await reader.GetInboxAsync(token, cancellationToken)
                : await reader.GetFolderAsync(token, selection.Id, cancellationToken);
            if (version != _folderVersion || cancellationToken.IsCancellationRequested) return;
            if (_graphPageCache.Count > 40) _graphPageCache.Clear();
            _graphPageCache[cacheKey] = page;
            _currentGraphMessages = page.Messages;
            if (_graphRows is null) ShowGraphMessages(page.Messages);
            else ReconcileGraphMessages(page.Messages);
            StatusText.Text = $"{page.Messages.Count} newest of {page.TotalCount:N0} {page.FolderName} items · " +
                $"{page.UnreadCount:N0} unread · {account.DisplayAddress}";
            ApplyFolderCounts(account, new Dictionary<string, int> { [selection.Id] = page.UnreadCount });      // the page already says how many are unread
            _ = RefreshMicrosoftFolderCountsAsync(account, cancellationToken);
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
        _graphRows = new ObservableCollection<GraphMessageListRow>(
            messages.Select(message => new GraphMessageListRow(WithLocalReadState(message))));
        var view = new DataGridCollectionView(_graphRows);
        if (GroupByDateCheck.IsChecked == true)
            view.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(GraphMessageListRow.DateGroup)));
        else if (_groupPath is not null)
            view.GroupDescriptions.Add(new DataGridPathGroupDescription(_groupPath));
        _updatingMessageList = true;
        try { MessageList.ItemsSource = view; RefreshItemCount(); MessageList.SelectedItem = null; }
        finally { _updatingMessageList = false; }
        TrackReadingPaneItem(); // leaving the previous folder's open item closes it out for read-tracking
    }

    private void ReconcileGraphMessages(IReadOnlyList<GraphInboxMessage> messages)
    {
        if (_graphRows is null) return;
        var incoming = messages.Select(message => message.Id).ToHashSet(StringComparer.Ordinal);
        for (var index = _graphRows.Count - 1; index >= 0; index--)
        {
            if (incoming.Contains(_graphRows[index].Message.Id)) continue;
            if (ReferenceEquals(MessageList.SelectedItem, _graphRows[index]))
            {
                MessageList.SelectedItem = null;
                Interlocked.Increment(ref _messageVersion);
                ClearReader();
            }
            _graphRows.RemoveAt(index);
        }
        for (var index = 0; index < messages.Count; index++)
        {
            var current = _graphRows.FirstOrDefault(row => row.Message.Id == messages[index].Id);
            if (current is null) _graphRows.Insert(index, new GraphMessageListRow(WithLocalReadState(messages[index])));
            else
            {
                current.Update(WithLocalReadState(messages[index]));
                var oldIndex = _graphRows.IndexOf(current);
                if (oldIndex != index) _graphRows.Move(oldIndex, index);
            }
            if (_activeGraphMessage?.Id == messages[index].Id)
                _activeGraphMessage = messages[index];
        }
    }

    private async void PreviewJunkImportClicked(object? sender, RoutedEventArgs e)
    {
        var files = await SafePick.FilesAsync(this, new FilePickerOpenOptions
        {
            Title = "Preview legacy OutlookJunkCleaner config (no import or cleaning)",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("JSON configuration") { Patterns = ["*.json"] }]
        }, failure => StatusText.Text = failure);
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
        MessageList.ItemsSource = null; RefreshItemCount();
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
        AttachmentLauncher.CleanUp();                            // copies opened in earlier runs
        InitMessageDrag();
        _currentMessages = messages;
        if (_activePath is { } archivePath)
            foreach (var message in messages)
                if (OverrideFor(PstMessageKey(archivePath, message.Nid)) is { } forced)
                    message.IsRead = forced;
        var rows = messages.Select(message => new MessageListRow(message)).ToArray();
        var view = new DataGridCollectionView(rows);
        if (GroupByDateCheck.IsChecked == true)
            view.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(MessageListRow.DateGroup)));
        else if (_groupPath is not null)
            view.GroupDescriptions.Add(new DataGridPathGroupDescription(_groupPath));
        _updatingMessageList = true;
        try
        {
            MessageList.ItemsSource = view; RefreshItemCount();
            MessageList.SelectedItem = null;
        }
        finally { _updatingMessageList = false; }
        TrackReadingPaneItem(); // leaving the previous folder's open item closes it out for read-tracking
    }

    private void GroupByDateChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressGroupToggle || (_currentMessages is null && _currentGraphMessages is null)) return;
        _groupPath = null;                                                                  // the tick means date sections again
        var messages = _currentMessages;
        var graphMessages = _currentGraphMessages;
        Interlocked.Increment(ref _messageVersion);
        ClearReader();
        if (messages is not null) ShowMessages(messages);
        else if (graphMessages is not null) ShowGraphMessages(graphMessages);
    }

    private void MessageListSorting(object? sender, DataGridColumnEventArgs e)
    {
        // Sorting by Received keeps the date sections: the order flips inside them and the sections themselves follow (oldest first when ascending).
        if (e.Column is DataGridColumn { SortMemberPath: "ReceivedSort" } && _groupPath is null) return;
        if (_groupPath is not null && MessageList.ItemsSource is DataGridCollectionView fieldView)
        {
            fieldView.GroupDescriptions.Clear();                                             // a column click sorts the whole folder, like the date sections do
            _groupPath = null;
            return;
        }
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

    private void MessageSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingMessageList) return;
        var version = Interlocked.Increment(ref _messageVersion);
        TrackReadingPaneItem();
        _messageSelectionTask = LoadSelectedMessageAsync(version);
    }

    private async Task LoadSelectedMessageAsync(long version)
    {
        _activeMessage = null;
        _activeGraphMessage = null;
        _currentGraphAttachments = null;
        ExportAttachmentButton.IsEnabled = false;
        ExportMessageButton.IsEnabled = false;
        if (MessageList.SelectedItem is GraphMessageListRow { Message: var gmailMessage } && _activeGmailFolder is { } gmailFolder)
        {
            await OpenGmailMessageAsync(gmailFolder, gmailMessage, version);
            return;
        }
        if (MessageList.SelectedItem is GraphMessageListRow { Message: var graphMessage } &&
            _activeMicrosoftAccount is { } account)
        {
            await OpenGraphMessageAsync(account, graphMessage, version);
            return;
        }
        if (MessageList.SelectedItem is not MessageListRow { Summary: var summary } ||
            _activePath is null || !_stores.TryGetValue(_activePath, out var store)) return;
        if (PreviewBlocked(summary)) { ShowPreviewBlocked(summary); return; }
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
            ExportMessageButton.IsEnabled = true;
            SubjectText.Text = message.Summary.Subject;
            SenderText.Text = message.Summary.From;
            RecipientText.Text = PstMessageHeader.Recipients(message);
            var pstDate = message.Summary.Received == DateTime.MinValue
                ? message.Summary.Sent : message.Summary.Received;
            MessageDateText.Text = pstDate == DateTime.MinValue ? "" : pstDate.ToString("ddd M/d/yyyy h:mm tt");
            SetReaderAvatar(message.Summary.From);
            ReaderReplyButton.IsVisible = ReaderReplyAllButton.IsVisible =
                ReaderForwardButton.IsVisible = false;
            AttachmentText.Text = PstMessageHeader.AttachmentLine(PstMessageHeader.VisibleAttachments(message));
            ShowAttachmentChips(null);
            ShowPstMessageBody(message);
            StatusText.Text = _richRuns is null ? "Message opened read-only." :
                "Message opened in rich-text view; images are loading automatically.";
        }
        catch (Exception ex) { if (version == _messageVersion) StatusText.Text = $"Could not read message: {ex.Message}"; }
    }

    private async void MessageListDoubleTapped(object? sender, TappedEventArgs e)
    {
        var row = e.Source is DataGridRow direct ? direct :
            (e.Source as Visual)?.FindAncestorOfType<DataGridRow>();
        if (row is null || !ReferenceEquals(row.DataContext, MessageList.SelectedItem)) return;
        if (MessageList.SelectedItem is GraphMessageListRow { Message: { IsDraft: true } draft } &&
            _activeMicrosoftAccount is { } account)
        {
            if (!account.CanWriteMicrosoftMail || !account.CanSendMicrosoftMail)
            {
                await ExplainMailActionAsync("Sign in again to edit this Microsoft draft.", account);
                return;
            }
            OpenCompose(account, null, draft.Id);
            return;
        }
        await OpenSelectedMessageWindowAsync();
    }

    private async Task OpenGraphMessageAsync(ConnectedAccount account, GraphInboxMessage message, long version)
    {
        StatusText.Text = "Reading Microsoft message…";
        try
        {
            var cancellationToken = _onlineCancellation?.Token ?? CancellationToken.None;
            GraphMessageBody? body;
            var localBody = await ReadLocalBodyAsync(account, message.Id);                    // the copy holds the body: no network wait
            var token = await GetMicrosoftSession(account).GetAccessTokenAsync(cancellationToken);
            if (localBody is not null) body = localBody.Html is not null ? new GraphMessageBody("html", localBody.Html) : new GraphMessageBody("text", localBody.Text);
            else body = await new GraphInboxReader(_graphHttp, account.AccountId).GetMessageBodyAsync(token, message.Id, cancellationToken);
            IReadOnlyList<GraphAttachment> attachments = [];
            var attachmentError = false;
            if (localBody is not null && message.HasAttachments)
            {
                // show the message now; the attachment chips follow when the server has answered
                if (version != _messageVersion || cancellationToken.IsCancellationRequested) return;
                ShowGraphMessageHeader(message);
                AttachmentText.Text = "Loading attachments…";
                SetMessageBody(localBody.Html, localBody.Html is null ? localBody.Text : "");
                StatusText.Text = "Microsoft message opened from the local copy.";
            }
            if (message.HasAttachments)
            {
                try
                {
                    attachments = await new GraphAttachmentReader(_graphHttp, account.AccountId)
                        .ListAsync(token, message.Id, cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { attachmentError = true; }
            }
            if (version != _messageVersion || cancellationToken.IsCancellationRequested) return;
            var bodyShown = localBody is not null && message.HasAttachments;               // already on screen; only the attachments are left to add
            if (!bodyShown) ShowGraphMessageHeader(message);
            _currentGraphAttachments = attachments;
            ExportAttachmentButton.IsEnabled = attachments.Any(CanSaveGraphAttachment);
            AttachmentText.Text = attachmentError ? "Could not load attachments. Select this message again to retry." : "";
            ShowAttachmentChips(attachments.Select(a => (a.Name, (long)a.SizeBytes, CanSaveGraphAttachment(a))));
            if (!bodyShown) SetMessageBody(string.Equals(body?.ContentType, "html", StringComparison.OrdinalIgnoreCase)
                    ? body?.Content : null,
                string.Equals(body?.ContentType, "text", StringComparison.OrdinalIgnoreCase)
                    ? body?.Content : body is null ? message.Preview : "");
            StatusText.Text = attachmentError ? "Message opened; attachments could not be loaded." :
                _richRuns is null ? "Microsoft message opened read-only." :
                "Microsoft message opened in rich-text view; images are loading automatically.";
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        { if (version == _messageVersion) StatusText.Text = "Could not read this message. Try signing in again from Account setup."; }
    }

    /// <summary>The header block of the reading pane for a connected-mailbox message (everything but the body and the attachment chips).</summary>
    private void ShowGraphMessageHeader(GraphInboxMessage message)
    {
        _activeGraphMessage = message;
        SubjectText.Text = message.Subject;
        SenderText.Text = message.From;
        RecipientText.Text = $"To   {message.To}";
        MessageDateText.Text = message.Received?.ToLocalTime().ToString("ddd M/d/yyyy h:mm tt") ?? "";
        SetReaderAvatar(message.From);
        ReaderReplyButton.IsVisible = ReaderReplyAllButton.IsVisible = ReaderForwardButton.IsVisible = true;
        HideFormatBar();
    }

    private async void ExportMessageClicked(object? sender, RoutedEventArgs e)
    {
        var message = _activeMessage;
        var path = _activePath;
        var version = _messageVersion;
        if (message is null || path is null || !_stores.TryGetValue(path, out var store)) return;
        // A directory picker avoids any provider-side creation or truncation of a target file.
        var folders = await SafePick.FoldersAsync(this, new FolderPickerOpenOptions
        {
            Title = "Choose folder for a new EML file", AllowMultiple = false
        }, failure => StatusText.Text = failure);
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
            StatusText.Text = "EML saved to a new file; PST unchanged.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or NotSupportedException or InvalidOperationException or PstException)
        { StatusText.Text = "EML export failed or target already exists; no existing file was overwritten."; }
        finally { ExportMessageButton.IsEnabled = ReferenceEquals(_activeMessage, message); }
    }

    private async void ExportFolderClicked(object? sender, RoutedEventArgs e)
    {
        if (_folderExportCancellation is not null ||
            FolderTree.SelectedItem is not TreeViewItem { Tag: FolderSelection selection } ||
            !_stores.TryGetValue(selection.Path, out var store)) return;
        var version = _folderVersion;
        var folders = await SafePick.FoldersAsync(this, new FolderPickerOpenOptions
        {
            Title = "Choose a parent folder for the new EML export directory (includes subfolders)",
            AllowMultiple = false
        }, failure => StatusText.Text = failure);
        if (version != _folderVersion || !_stores.TryGetValue(selection.Path, out var current) ||
            !ReferenceEquals(current, store)) return;
        var parent = folders.FirstOrDefault()?.TryGetLocalPath();
        if (parent is null) return;
        var destination = Path.Combine(parent, PstFolderEmlExporter.SuggestedDirectoryName(selection.Folder));
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            StatusText.Text = "An export folder with this name already exists there. Choose another parent folder or move the earlier export.";
            return;
        }
        using var cancellation = new CancellationTokenSource();
        _folderExportCancellation = cancellation;
        ExportFolderButton.IsEnabled = false;
        CancelFolderExportButton.IsEnabled = true;
        StatusText.Text = "Exporting PST folder and subfolders to EML…";
        var progress = new Progress<PstFolderExportProgress>(value =>
        {
            if (ReferenceEquals(_folderExportCancellation, cancellation))
                StatusText.Text = $"Exporting: {value.MessagesExported:N0} messages from {value.FoldersVisited:N0} folders…";
        });
        try
        {
            await _readerGate.WaitAsync(cancellation.Token);
            try
            {
                if (!_stores.TryGetValue(selection.Path, out current) || !ReferenceEquals(current, store)) return;
                var result = await Task.Run(() => PstFolderEmlExporter.ExportAsync(store, selection.Folder,
                    destination, progress, cancellation.Token), cancellation.Token);
                StatusText.Text = $"Exported {result.MessagesExported:N0} EML files from {result.FoldersExported:N0} folders to {Path.GetFileName(destination)}.";
            }
            finally { _readerGate.Release(); }
        }
        catch (OperationCanceledException) { StatusText.Text = "Folder export cancelled; no completed export folder was left."; }
        catch (PstFolderExportException error)
        { StatusText.Text = $"Folder export stopped: {error.Message} No completed export folder was left."; }
        catch (Exception) { StatusText.Text = "Folder export failed. Check the folder and message support; no completed export folder was left."; }
        finally
        {
            _folderExportCancellation = null;
            CancelFolderExportButton.IsEnabled = false;
            ExportFolderButton.IsEnabled = FolderTree.SelectedItem is TreeViewItem { Tag: FolderSelection };
        }
    }

    private void CancelFolderExportClicked(object? sender, RoutedEventArgs e)
    {
        CancelFolderExportButton.IsEnabled = false;
        _folderExportCancellation?.Cancel();
        StatusText.Text = "Cancelling folder export…";
    }

    private async void ExportAttachmentClicked(object? sender, RoutedEventArgs e)
    {
        if (_activeGmailFolder is { } gmailFolder && _gmailContent is { } gmailContent &&
            MessageList.SelectedItem is GraphMessageListRow { Message: var gmailMessage } && gmailMessage.Id == _gmailContentMessageId)
        {
            await SaveGmailAttachmentAsync(gmailFolder, gmailContent, gmailMessage.Id);
            return;
        }
        if (_activeGraphMessage is { } graphMessage && _activeMicrosoftAccount is { } graphAccount &&
            _currentGraphAttachments is { } graphAttachments)
        {
            await SaveGraphAttachmentAsync(graphAccount, graphMessage, graphAttachments);
            return;
        }
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
        var folders = await SafePick.FoldersAsync(this, new FolderPickerOpenOptions
        {
            Title = "Select where to export this attachment as a new file",
            AllowMultiple = false
        }, failure => StatusText.Text = failure);
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

    private static bool CanSaveGraphAttachment(GraphAttachment attachment)
    {
        if (attachment.Kind != GraphAttachmentKind.File || attachment.SizeBytes < 0 ||
            attachment.SizeBytes > GraphAttachmentReader.MaximumFileBytes) return false;
        try { PstAttachmentExporter.ValidateSuggestedFileName(attachment.Name); return true; }
        catch (ArgumentException) { return false; }
    }

    private async Task SaveGraphAttachmentAsync(ConnectedAccount account, GraphInboxMessage message,
        IReadOnlyList<GraphAttachment> attachments)
    {
        var version = _messageVersion;
        var candidates = attachments.Where(CanSaveGraphAttachment).ToArray();
        if (candidates.Length == 0) return;
        var chosen = _chipChoice is null ? null : candidates.FirstOrDefault(c => c.Name == _chipChoice);
        _chipChoice = null;
        var openIt = _openAttachment;
        _openAttachment = false;
        var attachment = chosen ?? (candidates.Length == 1 ? candidates[0] : await ChooseGraphAttachmentAsync(candidates));
        if (attachment is null || version != _messageVersion || !ReferenceEquals(_activeGraphMessage, message)) return;
        if (openIt && AttachmentLauncher.IsRisky(attachment.Name)) { StatusText.Text = "This file type can run code, so it is not opened from here. Use Save as… instead."; return; }
        string? directory;
        if (openIt) directory = AttachmentLauncher.NewDirectory();
        else
        {
            var folders = await SafePick.FoldersAsync(this, new FolderPickerOpenOptions
            {
                Title = "Choose where to save this attachment as a new file", AllowMultiple = false
            }, failure => StatusText.Text = failure);
            if (version != _messageVersion || !ReferenceEquals(_activeGraphMessage, message)) return;
            directory = folders.FirstOrDefault()?.TryGetLocalPath();
        }
        if (directory is null) return;
        ExportAttachmentButton.IsEnabled = false;
        try
        {
            var cancellationToken = _onlineCancellation?.Token ?? CancellationToken.None;
            var token = await GetMicrosoftSession(account).GetAccessTokenAsync(cancellationToken);
            if (version != _messageVersion || !ReferenceEquals(_activeGraphMessage, message)) return;
            await GraphAttachmentExporter.ExportAsync(
                new GraphAttachmentReader(_graphHttp, account.AccountId), token, message.Id, attachment,
                Path.Combine(directory, attachment.Name), cancellationToken);
            StatusText.Text = openIt ? AttachmentLauncher.Launch(Path.Combine(directory, attachment.Name)) ?? "Opened " + attachment.Name + "." : "Attachment saved to a new file.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Attachment save cancelled."; }
        catch (Exception) { StatusText.Text = "Could not save attachment. Check the connection or choose another folder; no existing file was overwritten."; }
        finally
        {
            ExportAttachmentButton.IsEnabled = ReferenceEquals(_activeGraphMessage, message) &&
                version == _messageVersion && attachments.Any(CanSaveGraphAttachment);
        }
    }

    private async Task<GraphAttachment?> ChooseGraphAttachmentAsync(IReadOnlyList<GraphAttachment> attachments)
    {
        var chooser = new Window { Title = "Choose an attachment", Width = 450, Height = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var list = new ListBox();
        foreach (var item in attachments)
            list.Items.Add(new ListBoxItem { Content = item.Name, Tag = item });
        var button = new Button { Content = "Save selected", Margin = new Thickness(8) };
        button.Click += (_, _) => chooser.Close((list.SelectedItem as ListBoxItem)?.Tag as GraphAttachment);
        chooser.Content = new DockPanel { Children = { list, button } };
        DockPanel.SetDock(button, Dock.Bottom);
        return await chooser.ShowDialog<GraphAttachment?>(this);
    }

    private async Task<MailAttachment?> ChooseAttachmentAsync(IReadOnlyList<MailAttachment> attachments)
    {
        var chooser = new Window { Title = "Choose an attachment", Width = 450, Height = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var list = new ListBox();
        foreach (var item in attachments)
            list.Items.Add(new ListBoxItem { Content = item.FileName, Tag = item });
        var button = new Button { Content = "Save selected", Margin = new Thickness(8) };
        button.Click += (_, _) => chooser.Close((list.SelectedItem as ListBoxItem)?.Tag as MailAttachment);
        chooser.Content = new DockPanel { Children = { list, button } };
        DockPanel.SetDock(button, Dock.Bottom);
        return await chooser.ShowDialog<MailAttachment?>(this);
    }

    private async void DetachClicked(object? sender, RoutedEventArgs e)
    {
        // Pick which open archive to close from a list - no dependence on what the folder tree
        // happens to have selected (navigating through Backstage can drop that selection).
        if (_stores.Count == 0)
        {
            StatusText.Text = "No Outlook Data Files are open.";
            return;
        }
        CloseBackstage();
        var chosen = await PickArchiveAsync("Close an Outlook Data File",
            "Select the Outlook Data File to close. The file itself is never deleted.");
        if (chosen is null) return;
        if (!_stores.TryGetValue(chosen, out var store))
        {
            StatusText.Text = "That archive is no longer open.";
            return;
        }
        // The gate avoids disposing while a reader operation is in flight.
        try { await DetachAsync(chosen, store); }
        catch (Exception ex) { StatusText.Text = $"Could not detach; the PST is still attached: {ex.Message}"; }
    }

    /// <summary>Simple list picker over the open archives (file name + full path), used by Close.</summary>
    private async Task<string?> PickArchiveAsync(string title, string instruction)
    {
        var dialog = new Window
        {
            Title = title, Width = 560, Height = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false
        };
        var list = new ListBox();
        foreach (var path in _stores.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            list.Items.Add(new ListBoxItem
            {
                Content = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock { Text = Path.GetFileName(path), FontWeight = Avalonia.Media.FontWeight.SemiBold },
                        new TextBlock { Text = path, FontSize = 11, Foreground = Avalonia.Media.Brushes.Gray }
                    }
                },
                Tag = path
            });
        }
        var ok = new Button { Content = "Close File", IsEnabled = false, MinWidth = 100 };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        list.SelectionChanged += (_, _) => ok.IsEnabled = list.SelectedItem is ListBoxItem;
        ok.Click += (_, _) => dialog.Close((list.SelectedItem as ListBoxItem)?.Tag as string);
        cancel.Click += (_, _) => dialog.Close((string?)null);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16), Spacing = 10, Children =
            {
                new TextBlock { Text = instruction, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                list,
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { ok, cancel } }
            }
        };
        return await dialog.ShowDialog<string?>(this);
    }

    private async Task DetachAsync(string path, IPstEngine store)
    {
        Interlocked.Increment(ref _folderVersion);
        Interlocked.Increment(ref _messageVersion);
        _searchCancellation?.Cancel();
        await _readerGate.WaitAsync();
        try
        {
            if (!_stores.ContainsKey(path)) return;
            _attachedPstStore.Remove(path);
            _stores.Remove(path);
            store.Dispose();
            foreach (var root in FolderTree.Items.OfType<TreeViewItem>().ToArray())
                if (Equals(root.Tag, path)) FolderTree.Items.Remove(root);
            _activePath = null;
            _activeFolder = null;
            _currentMessages = null;
            MessageList.ItemsSource = null; RefreshItemCount();
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

    private async void ShortcutsClicked(object? sender, RoutedEventArgs e)
    {
        var updated = await new ShortcutOptionsWindow(_shortcuts).ShowDialog<ShortcutSettings?>(this);
        if (updated is null) return;
        try
        {
            _shortcutStore.Save(updated);
            _shortcuts = updated;
            StatusText.Text = "Keyboard shortcuts saved.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { StatusText.Text = "Could not save keyboard shortcuts. Check access to the OpenOutlook settings folder."; }
    }

    private async void MessageListShortcutKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _mailActionBusy) return;
        // Shift+Delete on archive rows = permanent delete (plain Delete follows the ribbon path:
        // move to Deleted Items). Done before shortcut matching so the modifier is not lost.
        if (e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.Shift &&
            MessageList.SelectedItem is MessageListRow)
        {
            e.Handled = true;
            await TryHandlePstDeleteAsync("delete", permanent: true);
            return;
        }
        var shortcut = _shortcuts.Bindings.FirstOrDefault(binding =>
            binding.Key == e.Key && binding.Modifiers == e.KeyModifiers);
        if (shortcut is null) return;
        e.Handled = true;
        await ExecuteMailActionAsync(shortcut.Action);
    }


    /// <summary>Outlook's own avatar palette, so a sender keeps the same colour across messages.</summary>
    private static readonly string[] AvatarColors =
        ["#C75B12", "#0F6CBD", "#107C41", "#8764B8", "#D83B01", "#038387", "#CA5010", "#5C2D91"];

    /// <summary>Initials circle in the reading pane header, coloured from the sender's name.</summary>
    private void SetReaderAvatar(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            ReaderAvatar.IsVisible = false;
            return;
        }
        ReaderAvatarInitials.Text = InitialsOf(displayName);
        var hash = 0;
        foreach (var c in displayName) hash = (hash * 31 + c) & 0x3FFFFFFF;
        ReaderAvatar.Background = Brush.Parse(AvatarColors[hash % AvatarColors.Length]);
        ReaderAvatar.IsVisible = true;
    }

    /// <summary>First letters of the first and last words, ignoring a trailing address in brackets.</summary>
    internal static string InitialsOf(string displayName)
    {
        var core = displayName.Split('<')[0].Trim();
        if (core.Length == 0) core = displayName.Trim();
        if (core.Length == 0) return "?";
        var words = core.Split(new[] { ' ', '.', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "?";
        if (words.Length == 1)
            return words[0].Length >= 2 ? words[0][..2].ToUpperInvariant() : words[0].ToUpperInvariant();
        return string.Concat(words[0][0], words[^1][0]).ToUpperInvariant();
    }

    private void RefreshItemCount()
    {
        // DataGrid exposes no public item count, so the bound collection is counted directly.
        var count = MessageList.ItemsSource switch
        {
            System.Collections.ICollection collection => collection.Count,
            _ => 0,
        };
        MessageCountText.Text = count == 0 ? "" : $"Items: {count:N0}";
    }

    private void UpdateZoomUi()
    {
        var percent = (int)Math.Round(_htmlZoom * 100);
        ZoomText.Text = percent == 100 ? "" : $"Zoom {percent}%";
        ZoomSlider.Value = Math.Clamp(percent, ZoomSlider.Minimum, ZoomSlider.Maximum);
    }

    private void SearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SearchClicked(sender, e);
    }

    private void ListFilterUnreadPressed(object? sender, PointerPressedEventArgs e) =>
        StatusText.Text = "Showing only unread messages is planned; every message in the folder is listed.";

    private void ApplyAppearance()
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = _appearance.DarkMode ? ThemeVariant.Dark : ThemeVariant.Light;
        // These named high-contrast accents avoid arbitrary user hex colors with illegible foregrounds.
        // The override goes on the window's own resources: those take precedence over the application
        // palette and its theme dictionaries, so one assignment restyles every use of the accent.
        var accent = _appearance.Accent switch
        {
            "Green" => "#107C41", "Purple" => "#5C2D91", "Orange" => "#CA5010", _ => "#0F6CBD"
        };
        Resources["OlAccent"] = new SolidColorBrush(Color.Parse(accent));
        var size = _appearance.TextSize;
        FontSize = size;
        RibbonTabs.FontSize = size;
        FolderTree.FontSize = MessageList.FontSize = BodyText.FontSize = size;
        SubjectText.FontSize = size + 4;
        SenderText.FontSize = size;
        RecipientText.FontSize = AttachmentText.FontSize = Math.Max(10, size - 1);
        BodyViewButton.FontSize = size;
        StatusText.FontSize = Math.Max(10, size - 1);
        // The backstage keeps its own fixed type sizes, as classic Outlook's does.
        ArchiveSearchButton.FontSize = ExportAttachmentButton.FontSize = ExportMessageButton.FontSize =
            ExportFolderButton.FontSize = CancelFolderExportButton.FontSize = PreviewJunkImportButton.FontSize =
            RefreshInboxButton.FontSize = ArchiveSearchBox.FontSize =
            Math.Max(10, size - 1);        if (_richRuns is not null) ShowMessageBody();
    }

    private void ApplyViewLayout(ViewLayoutSettings settings)
    {
        Width = settings.WindowWidth;
        Height = settings.WindowHeight;
        _normalWindowWidth = settings.WindowWidth;
        _normalWindowHeight = settings.WindowHeight;
        _lastWindowMaximized = settings.WindowMaximized;
        PaneGrid.ColumnDefinitions[0].Width = new GridLength(settings.FolderPaneWeight, GridUnitType.Star);
        PaneGrid.ColumnDefinitions[2].Width = new GridLength(settings.MessagePaneWeight, GridUnitType.Star);
        PaneGrid.ColumnDefinitions[4].Width = new GridLength(settings.ReaderPaneWeight, GridUnitType.Star);
        foreach (var column in settings.Columns.OrderBy(column => column.DisplayIndex))
        {
            var target = MessageList.Columns[column.ColumnIndex];
            if (settings.Columns.Count == MessageList.Columns.Count) target.IsVisible = column.Visible;
            target.DisplayIndex = column.DisplayIndex;
            target.Width = new DataGridLength(column.Width,
                column.IsStar ? DataGridLengthUnitType.Star : DataGridLengthUnitType.Pixel);
        }
    }

    private void RestoreWindowPlacement(ViewLayoutSettings settings)
    {
        if (settings.WindowX is { } x && settings.WindowY is { } y &&
            Screens.All.Any(screen => screen.WorkingArea.Contains(new PixelPoint(x + 40, y + 20))))
            Position = new PixelPoint(x, y);
        _normalWindowPosition = Position;
        if (settings.WindowMaximized) WindowState = WindowState.Maximized;
        _layoutReady = true;
    }

    private void TrackWindowGeometry()
    {
        if (!_layoutReady) return;
        if (WindowState == WindowState.Normal)
        {
            if (Bounds.Width >= MinWidth && Bounds.Height >= MinHeight)
            {
                _normalWindowWidth = Bounds.Width;
                _normalWindowHeight = Bounds.Height;
            }
            _normalWindowPosition = Position;
            _lastWindowMaximized = false;
        }
        else if (WindowState == WindowState.Maximized)
            _lastWindowMaximized = true;
        ScheduleLayoutSave();
    }

    private void ScheduleLayoutSave()
    {
        if (!_layoutReady) return;
        _layoutSaveTimer.Stop();
        _layoutSaveTimer.Start();
    }

    private void PersistViewLayout()
    {
        var folder = PaneGrid.ColumnDefinitions[0].ActualWidth;
        var messages = PaneGrid.ColumnDefinitions[2].ActualWidth;
        var reader = PaneGrid.ColumnDefinitions[4].ActualWidth;
        var total = folder + messages + reader;
        if (total <= 0 || _portraitReading) return;   // the collapsed portrait-reading widths are temporary
        var columns = MessageList.Columns.Select((column, index) =>
        {
            var isStar = column.Width.UnitType == DataGridLengthUnitType.Star;
            var width = column.Width.UnitType is DataGridLengthUnitType.Pixel or DataGridLengthUnitType.Star
                ? column.Width.Value : column.ActualWidth;
            return new MessageColumnLayout(index, column.DisplayIndex, width, isStar, column.IsVisible);
        }).ToArray();
        var settings = new ViewLayoutSettings
        {
            WindowWidth = _normalWindowWidth,
            WindowHeight = _normalWindowHeight,
            WindowX = _normalWindowPosition?.X,
            WindowY = _normalWindowPosition?.Y,
            WindowMaximized = _lastWindowMaximized,
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
        ClearInlineImages();
        _bodyHtml = string.IsNullOrWhiteSpace(html) ? null : html;
        _showOriginalHtml = false;
        // Connected mail starts on the snapshot reader. The interactive web view renders real HTML,
        // so its text can be selected and copied, but on the owner's desktop it accepts the document
        // and paints nothing: the reading pane goes blank while the header, buttons and content
        // probe all report success. That failure is invisible to every check available here -- the
        // probe runs in the page, and a native child surface cannot be captured from Avalonia -- so
        // there is no way to detect it and fall back automatically. Opting in with "Use alternate
        // reader" keeps the risk with the person who can see it.
        //
        // Making this the default again needs the blank paint fixed first, not a fallback. Suspect
        // is WebKitGTK's accelerated path on this compositor; WEBKIT_DISABLE_DMABUF_RENDERER=1 and
        // WEBKIT_DISABLE_COMPOSITING_MODE=1 are the usual levers. Note that a headless Xvfb run
        // renders interactive mode fine, so it does NOT reproduce this and proves nothing either way.
        // Every HTML message starts on the snapshot reader, archived mail included. The embedded web
        // view cannot composite into an Avalonia window under Wayland -- it loads the document and its
        // scripts run, but nothing is painted -- so preferring it for PST archives meant archive
        // messages looked empty while their content probe reported success. Connected mail already
        // defaulted here; leaving the other branch on the broken path was an oversight, not a choice.
        // Start on the embedded web view where it paints (Windows, macOS, X11) and on the snapshot elsewhere (Wayland); either one
        // falls back to the other by itself. See ReaderEngineChoice.
        _preferSnapshotForMessage = !ReaderEngineChoice.PreferEmbedded();
        _htmlImageSources = [];
        _failedMessageImages = 0;
        _richRuns = null;
        _bodyPlain = plain ?? "";
        _showRichBody = true;
        if (!string.IsNullOrWhiteSpace(html))
        {
            try
            {
                _richRuns = SafeHtmlPreview.Parse(html);
                _htmlImageSources = SafeHtmlDocument.FindImages(html);
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
        ReaderScrollViewer.Offset = new Vector(0, 0);
        ShowMessageBody();
        if (_bodyHtml is not null)
        {
            HtmlStatusText.Text = "Laying out HTML message…";
            HtmlStatusText.IsVisible = true;
            _ = RenderBrowserHtmlAsync(_messageVersion);
            if (_htmlImageSources.Count > 0)
                _imageLoadTask = LoadImagesAsync(_messageVersion);
        }
    }

    private void ToggleBodyViewClicked(object? sender, RoutedEventArgs e)
    {
        if (_richRuns is null && _bodyHtml is null) return;
        _showRichBody = !_showRichBody;
        ShowMessageBody();
    }

    private void ReaderModeClicked(object? sender, RoutedEventArgs e)
    {
        if (_bodyHtml is null) return;
        _preferSnapshotForMessage = !_preferSnapshotForMessage;
        _embeddedHtmlActive = false;
        MainHtmlWebView.IsVisible = false;
        HtmlStatusText.Text = "Loading message in the alternate reader…";
        HtmlStatusText.IsVisible = true;
        ShowMessageBody();
        _ = RenderBrowserHtmlAsync(_messageVersion);
    }

    private async void PopOutMessageClicked(object? sender, RoutedEventArgs e) =>
        await OpenSelectedMessageWindowAsync();

    private async Task OpenSelectedMessageWindowAsync()
    {
        var version = _messageVersion;
        var selected = MessageList.SelectedItem;
        try { await _messageSelectionTask; }
        catch (Exception) { /* The selection handler reports the read failure. */ }
        if (version != _messageVersion || !ReferenceEquals(selected, MessageList.SelectedItem)) return;
        if (_activeMessage is null && _activeGraphMessage is null && !(_activeGmailFolder is not null && _gmailContent is not null))
        {
            StatusText.Text = "Could not open this message. Select it again and retry.";
            return;
        }
        var images = _inlineImageBytes.ToDictionary(pair => pair.Key, pair => pair.Value,
            StringComparer.Ordinal);
        var account = _activeMicrosoftAccount;
        var graphMessage = _activeGraphMessage;
        MessageWindow? window = null;
        var gmailFolder = _activeGmailFolder is { Account.CanSendGmail: true } g && _gmailContent is not null && _activeGraphMessage is null && _activeMessage is null ? g : null;
        Action<string>? respond = account is not null && graphMessage is not null
            ? action => RespondFromMessageWindow(account, graphMessage.Id, action, window!)
            : gmailFolder is not null ? action => _ = ComposeGmailAsync(gmailFolder, action) : null;
        window = new MessageWindow(SubjectText.Text ?? "Message", SenderText.Text ?? "",
            RecipientText.Text ?? "", _bodyPlain, _bodyHtml, images, _showOriginalHtml, respond);
        window.Show(this);
        if (_imageLoadTask is { IsCompleted: false } imageLoadTask)
            _ = UpdatePopOutImagesAsync(window, version, imageLoadTask);
        StatusText.Text = "Message opened in a separate window.";
    }

    private async Task UpdatePopOutImagesAsync(MessageWindow window, long version, Task imageLoadTask)
    {
        try { await imageLoadTask; }
        catch (Exception) { return; }
        if (version == _messageVersion && window.IsVisible)
            window.UpdateImages(_inlineImageBytes);
    }

    private async void OpenInteractiveReaderClicked(object? sender, RoutedEventArgs e)
    {
        if (_bodyHtml is not { } html) return;
        var version = _messageVersion;
        if (_imageLoadTask is { } images)
        {
            try { await images; }
            catch (Exception) { /* Open with the images available so far. */ }
        }
        if (version != _messageVersion) return;
        try
        {
            var document = SafeHtmlDocument.BuildInteractive(html, _inlineImageBytes);
            var dialog = new NativeWebDialog
            {
                Title = (SubjectText.Text ?? "Message") + " — OpenOutlook",
                CanUserResize = true
            };
            dialog.NavigationStarted += (_, args) =>
            {
                // Fail closed. An unclassifiable target used to fall through without cancelling, so a
                // link the classifier could not vet would navigate inside this window -- one opened for
                // trusted mail content, with the subject in its title bar. Only the document's own
                // about: load is allowed; everything else is either handed to the system browser or
                // blocked outright. This mirrors the reader web view's guard above.
                if (SafeHtmlDocument.TryLink(args.Request?.AbsoluteUri, out var url))
                {
                    args.Cancel = true;
                    OpenExternal(url);
                }
                else if (args.Request is { Scheme: not "about" }) args.Cancel = true;
            };
            dialog.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (SafeHtmlDocument.TryLink(args.Request?.AbsoluteUri, out var url)) OpenExternal(url);
            };
            dialog.Closing += (_, _) => _interactiveDialogs.Remove(dialog);
            _interactiveDialogs.Add(dialog);
            dialog.Show(this);
            dialog.NavigateToString(document);
            StatusText.Text = "Interactive message opened. You can select and copy formatted text.";
        }
        catch (Exception)
        { StatusText.Text = "Interactive HTML is unavailable here; the standard reader remains available."; }

        void OpenExternal(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception) { StatusText.Text = "Could not open this link in the system browser."; }
        }
    }

    private async void SavePrintablePdfClicked(object? sender, RoutedEventArgs e)
    {
        var version = _messageVersion;
        if (_imageLoadTask is { } images)
        {
            try { await images; }
            catch (Exception) { /* Print the images that were available. */ }
        }
        if (version != _messageVersion) return;
        var folders = await SafePick.FoldersAsync(this, new FolderPickerOpenOptions
        { Title = "Choose a folder for a new printable PDF", AllowMultiple = false },
        failure => StatusText.Text = failure);
        if (version != _messageVersion || folders.FirstOrDefault()?.TryGetLocalPath() is not { } folder) return;
        PrintablePdfButton.IsEnabled = false;
        try
        {
            var document = _bodyHtml is { } html ? SafeHtmlDocument.Build(html, _inlineImageBytes) :
                "<html><body><pre style='white-space:pre-wrap'>" + System.Net.WebUtility.HtmlEncode(_bodyPlain) +
                "</pre></body></html>";
            var pdf = await BrowserHtmlRenderer.RenderPdfAsync(document);
            if (version != _messageVersion) return;
            var path = Path.Combine(folder, "message-" + Guid.NewGuid().ToString("N") + ".pdf");
            var temporary = path + ".tmp";
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                Share = FileShare.None };
            if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            try
            {
                await using (var output = new FileStream(temporary, options))
                {
                    await output.WriteAsync(pdf);
                    output.Flush(flushToDisk: true);
                }
                File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            StatusText.Text = "Printable PDF saved. Open it in your PDF viewer to print.";
        }
        catch (Exception)
        { StatusText.Text = "Could not create the printable PDF. Check the folder and browser installation."; }
        finally { PrintablePdfButton.IsEnabled = true; }
    }

    private void ToggleOriginalClicked(object? sender, RoutedEventArgs e)
    {
        if (_bodyHtml is null) return;
        _showOriginalHtml = !_showOriginalHtml;
        _showRichBody = true;
        ReaderScrollViewer.Offset = new Vector(0, 0);
        HtmlStatusText.Text = _showOriginalHtml
            ? "Loading original message and external content…" : "Laying out HTML message…";
        HtmlStatusText.IsVisible = true;
        ShowMessageBody();
        _ = RenderBrowserHtmlAsync(_messageVersion);
    }

    private async Task LoadImagesAsync(long version)
    {
        var loadLimit = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        _inlineImageLoadCancellation = loadLimit;
        try
        {
            var images = _htmlImageSources.ToArray();
            var loaded = 0;
            var totalBytes = 0;
            var memoryLimited = 0;
            // Reserve declared pixels up front: Validate() trusts the size a file declares, and the
            // encoded-byte cap below cannot see a small file that decodes to a huge bitmap.
            var pixelBudget = new DecodedPixelBudget(SafeInlineImage.MaximumMessagePixels);
            using var remoteClient = SafeRemoteImageLoader.CreateClient();
            for (var offset = 0; offset < images.Length; offset += 4)
            {
                if (loadLimit.IsCancellationRequested || version != _messageVersion) break;
                var batch = images.Skip(offset).Take(4).ToArray();
                var results = await Task.WhenAll(batch.Select(async source =>
                {
                    try
                    {
                        return source.Key.StartsWith("cid:", StringComparison.Ordinal)
                            ? await ReadInlineImageAsync(source.Value)
                            : source.ContactsExternalSite
                                ? await SafeRemoteImageLoader.FetchAsync(source.Value, remoteClient, loadLimit.Token)
                                : SafeHtmlDocument.DecodeDataImage(source);
                    }
                    catch (Exception) { return null; }
                }));
                if (loadLimit.IsCancellationRequested || version != _messageVersion) return;
                for (var index = 0; index < batch.Length; index++)
                {
                    var bytes = results[index];
                    if (bytes is null) continue;
                    try
                    {
                        var declared = SafeInlineImage.Validate(bytes);
                        if (!pixelBudget.TryReserve(declared.Width * (long)declared.Height))
                        { memoryLimited++; continue; }
                        if (totalBytes + bytes.Length > 48 * 1024 * 1024) break;
                        var bitmap = new Bitmap(new MemoryStream(bytes, writable: false));
                        if (bitmap.PixelSize.Width * (long)bitmap.PixelSize.Height > SafeInlineImage.MaximumPixels)
                        { bitmap.Dispose(); continue; }
                        _inlineImages[batch[index].Key] = bitmap;
                        _inlineImageBytes[batch[index].Key] = bytes;
                        totalBytes += bytes.Length;
                        loaded++;
                    }
                    catch (Exception) { /* Damaged image: leave its placeholder. */ }
                }
                if (totalBytes >= 48 * 1024 * 1024) break;
            }
            if (loadLimit.IsCancellationRequested || version != _messageVersion || !IsVisible) return;
            _failedMessageImages = images.Length - loaded;
            HtmlStatusText.Text = "Laying out HTML message with images…";
            HtmlStatusText.IsVisible = true;
            ShowMessageBody();
            _ = RenderBrowserHtmlAsync(version);
            StatusText.Text = loaded == images.Length ?
                $"Showing {loaded} message image{(loaded == 1 ? "" : "s")}." :
                memoryLimited > 0 ?
                    $"Showing {loaded} of {images.Length} message images; the largest were skipped to limit memory." :
                    $"Showing {loaded} of {images.Length} message images; some could not be loaded.";
        }
        finally
        {
            if (ReferenceEquals(_inlineImageLoadCancellation, loadLimit))
                _inlineImageLoadCancellation = null;
            loadLimit.Dispose();
        }
    }

    private static string ImageKey(HtmlPreviewRun run) => run.ImageContentId is { } cid ? "cid:" + cid.ToLowerInvariant() :
        "remote:" + run.RemoteImageUrl;

    private async Task<byte[]?> ReadInlineImageAsync(string id)
    {
        static string Normalize(string value) => value.Trim().Trim('<', '>');
        if (_activeMessage is { } pstMessage && _activePath is { } path && _stores.TryGetValue(path, out var store))
        {
            var attachment = pstMessage.Attachments.FirstOrDefault(a => a.Method == 1 &&
                a.Size is >= 0 and <= SafeInlineImage.MaximumBytes &&
                string.Equals(Normalize(a.ContentId), id, StringComparison.OrdinalIgnoreCase));
            if (attachment is null) return null;
            await _readerGate.WaitAsync();
            try { return await Task.Run(() => store.ReadAttachmentData(pstMessage.Summary, attachment, SafeInlineImage.MaximumBytes)); }
            finally { _readerGate.Release(); }
        }
        if (_activeGraphMessage is { } graphMessage && _activeMicrosoftAccount is { } account &&
            _currentGraphAttachments is { } attachments)
        {
            var attachment = attachments.FirstOrDefault(a => a.IsInline && a.Kind == GraphAttachmentKind.File &&
                a.SizeBytes is >= 0 and <= SafeInlineImage.MaximumBytes &&
                string.Equals(Normalize(a.ContentId ?? ""), id, StringComparison.OrdinalIgnoreCase));
            if (attachment is null) return null;
            var cancellationToken = _onlineCancellation?.Token ?? CancellationToken.None;
            var token = await GetMicrosoftSession(account).GetAccessTokenAsync(cancellationToken);
            using var output = new LimitedImageStream();
            await new GraphAttachmentReader(_graphHttp, account.AccountId)
                .CopyFileAsync(token, graphMessage.Id, attachment, output, cancellationToken);
            return output.ToArray();
        }
        return null;
    }

    private sealed class LimitedImageStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Length + buffer.Length > SafeInlineImage.MaximumBytes)
                throw new InvalidDataException("Embedded image exceeds the preview limit.");
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    private void ClearInlineImages()
    {
        _failedMessageImages = 0;
        _embeddedHtmlActive = false;
        _embeddedNavigation?.TrySetCanceled();
        _embeddedNavigation = null;
        MainHtmlWebView.IsVisible = false;
        var imageCancellation = _inlineImageLoadCancellation;
        _inlineImageLoadCancellation = null;
        _imageLoadTask = null;
        imageCancellation?.Cancel();
        var renderCancellation = _htmlRenderCancellation;
        _htmlRenderCancellation = null;
        renderCancellation?.Cancel();
        HtmlPagesPanel.Children.Clear();
        HtmlPagesPanel.IsVisible = false;
        foreach (var bitmap in _htmlPageBitmaps) bitmap.Dispose();
        _htmlPageBitmaps.Clear();
        _htmlPagePngs = [];
        foreach (var image in _inlineImages.Values) image.Dispose();
        _inlineImages.Clear();
        _inlineImageBytes.Clear();
    }

    private async Task RenderBrowserHtmlAsync(long version)
    {
        if (_bodyHtml is not { } html) return;
        var previousCancellation = _htmlRenderCancellation;
        _htmlRenderCancellation = null;
        previousCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _htmlRenderCancellation = cancellation;
        try
        {
            if (!_preferSnapshotForMessage)
            {
                var interactive = _showOriginalHtml ? html : SafeHtmlDocument.BuildInteractive(html, _inlineImageBytes);
                if (await TryShowEmbeddedHtmlAsync(interactive, version, cancellation.Token)) return;
            }
            cancellation.Token.ThrowIfCancellationRequested();
            var document = _showOriginalHtml ? html : SafeHtmlDocument.Build(html, _inlineImageBytes);
            var width = (int)Math.Clamp(ReaderPane.Bounds.Width - 36, 400, 1200);
            var rendered = await BrowserHtmlRenderer.RenderDocumentAsync(document, width, cancellation.Token,
                trustedOriginal: _showOriginalHtml);
            if (cancellation.IsCancellationRequested || version != _messageVersion) return;
            var bitmaps = rendered.Pages.Select(png => new Bitmap(new MemoryStream(png, writable: false))).ToArray();
            HtmlPagesPanel.Children.Clear();
            foreach (var bitmap in _htmlPageBitmaps) bitmap.Dispose();
            _htmlPageBitmaps.Clear();
            _htmlPageBitmaps.AddRange(bitmaps);
            _htmlPagePngs = rendered.Pages;
            double top = 0;
            foreach (var bitmap in bitmaps)
            {
                HtmlPagesPanel.Children.Add(new HtmlPageView(bitmap, top, rendered.Links,
                    message => StatusText.Text = message, rendered.Text));
                top += bitmap.PixelSize.Height;
            }
            FitHtmlPageImage();
            ShowImageFailureStatus();
            ShowMessageBody();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Error("reader", ex, "the HTML snapshot reader failed");
            if (!cancellation.IsCancellationRequested && version == _messageVersion)
            {
                StatusText.Text = ex is NotSupportedException
                    ? "Full HTML layout needs Chrome or Chromium; showing a basic preview."
                    : "Full HTML layout is unavailable; showing a basic preview.";
                HtmlStatusText.Text = StatusText.Text;
                HtmlStatusText.IsVisible = true;
            }
        }
        finally
        {
            if (ReferenceEquals(_htmlRenderCancellation, cancellation))
                _htmlRenderCancellation = null;
            cancellation.Dispose();
        }
    }

    private bool _webViewAvailable = true;

    /// <summary>The embedded web view needs WebKitGTK on Linux. Without it the control throws while it attaches (a crash dialog on every
    /// launch), so it is taken out of the tree and the reader uses the snapshot / text renderers, which need no native browser.</summary>
    private void RemoveWebViewIfUnsupported()
    {
        if (!OperatingSystem.IsLinux()) return;
        foreach (var name in new[] { "libwebkit2gtk-4.1.so.0", "libwebkit2gtk-4.0.so.37" })
            if (System.Runtime.InteropServices.NativeLibrary.TryLoad(name, out var handle))
            {
                System.Runtime.InteropServices.NativeLibrary.Free(handle);
                return;
            }
        _webViewAvailable = false;
        if (MainHtmlWebView.Parent is Panel panel) panel.Children.Remove(MainHtmlWebView);
        AppLog.Note("reader", "WebKitGTK is not installed; the interactive reader is off and the snapshot reader is used");
    }

    private async Task<bool> TryShowEmbeddedHtmlAsync(string document, long version, CancellationToken cancellationToken)
    {
        if (!_webViewAvailable) return false;
        if (version != _messageVersion) return false;
        // WebView2's NavigateToString refuses documents over 2 MB (it throws "Value does not fall within the expected range"), and a message whose pictures are
        // embedded as data URIs passes that easily (a 36-picture Amazon mailing did): such a message goes straight to the snapshot reader.
        if (document.Length > EmbeddedHtmlMaximumCharacters || (document.Length > 500_000 && System.Text.Encoding.UTF8.GetByteCount(document) > 1_900_000))
        {
            AppLog.Note("reader", $"interactive reader skipped: the document is {document.Length:N0} characters (WebView2 accepts about 2 MB); using the snapshot reader");
            _preferSnapshotForMessage = true;
            _embeddedHtmlActive = false;                                  // an earlier layout of this message (before its pictures arrived) may still be showing in the web view: take it away
            MainHtmlWebView.IsVisible = false;
            StatusText.Text = "This message is too large for the interactive reader; showing the alternate view.";
            return false;
        }
        var succeeded = false;
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _embeddedNavigation = completed;
            _embeddedHtmlActive = true;
            MainHtmlWebView.IsVisible = true;
            MainHtmlWebView.NavigateToString(document);
            var timeout = Task.Delay(TimeSpan.FromSeconds(6), cancellationToken);
            if (await Task.WhenAny(completed.Task, timeout) != completed.Task ||
                cancellationToken.IsCancellationRequested || version != _messageVersion)
            { AppLog.Note("reader", "interactive reader: the page did not finish loading within 6 seconds"); return false; }
            var probeTask = MainHtmlWebView.InvokeScript(
                "document.body && (document.body.innerText.trim().length > 0 || document.images.length > 0)");
            if (await Task.WhenAny(probeTask, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken)) != probeTask)
            { AppLog.Note("reader", "interactive reader: the page did not answer the content probe"); return false; }
            var probe = await probeTask;
            if (!string.Equals(probe?.Trim('"'), "true", StringComparison.OrdinalIgnoreCase)) { AppLog.Note("reader", "interactive reader: the page has no text or images (probe said " + probe + ")"); return false; }
            ShowImageFailureStatus();
            ShowMessageBody();
            StatusText.Text = "Interactive HTML ready in the reading pane.";
            succeeded = true;
            return true;
        }
        catch (Exception ex)
        { AppLog.Error("reader", ex, "interactive reader failed"); return false; }
        finally
        {
            if (ReferenceEquals(_embeddedNavigation, completed))
            {
                _embeddedNavigation = null;
                if (!succeeded && version == _messageVersion && !cancellationToken.IsCancellationRequested)
                {
                    _embeddedHtmlActive = false;
                    MainHtmlWebView.IsVisible = false;
                    _preferSnapshotForMessage = true;
                    StatusText.Text = "Interactive reader could not display this message; showing the alternate view.";
                }
            }
        }
    }

    private void ShowImageFailureStatus()
    {
        HtmlStatusText.IsVisible = _failedMessageImages > 0;
        if (_failedMessageImages > 0)
            HtmlStatusText.Text = _failedMessageImages == 1
                ? "One message image could not be loaded. The rest of the message is available."
                : $"{_failedMessageImages} message images could not be loaded. The rest of the message is available.";
    }

    private void OpenReaderLink(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) { StatusText.Text = "Could not open this link in the system browser."; }
    }

    private void FitHtmlPageImage()
    {
        if (_htmlPageBitmaps.Count == 0) return;
        var availableWidth = Math.Max(200, ReaderPane.Bounds.Width - 36);
        foreach (var (bitmap, index) in _htmlPageBitmaps.Select((bitmap, index) => (bitmap, index)))
        {
            var scale = Math.Min(1, availableWidth / bitmap.PixelSize.Width) * _htmlZoom;
            ((HtmlPageView)HtmlPagesPanel.Children[index]).SetScale(scale);
        }
    }

    private void ZoomInClicked(object? sender, RoutedEventArgs e)
    {
        _htmlZoom = Math.Min(2.5, Math.Round(_htmlZoom * 1.25, 2));
        UpdateZoomUi();
        FitHtmlPageImage();
        ApplyEmbeddedZoom();
    }

    private void ZoomOutClicked(object? sender, RoutedEventArgs e)
    {
        _htmlZoom = Math.Max(0.5, Math.Round(_htmlZoom / 1.25, 2));
        UpdateZoomUi();
        FitHtmlPageImage();
        ApplyEmbeddedZoom();
    }

    private void ZoomFitClicked(object? sender, RoutedEventArgs e)
    {
        _htmlZoom = 1;
        UpdateZoomUi();
        FitHtmlPageImage();
        ApplyEmbeddedZoom();
    }

    private void ApplyEmbeddedZoom()
    {
        if (!_embeddedHtmlActive) return;
        try
        {
            var percent = (int)Math.Round(_htmlZoom * 100);
            MainHtmlWebView.InvokeScript($"document.documentElement.style.zoom = '{percent}%';");
        }
        catch (Exception) { /* The content remains readable if this engine cannot zoom. */ }
    }


    /// <summary>
    /// Opens the sanitised message document in the system browser. The reading pane cannot offer text
    /// selection on every display server -- an embedded web view does not composite into an Avalonia
    /// window under Wayland -- while a browser has a real HTML engine and gives selection, copying,
    /// find-in-page and printing for free. The document is the same one the snapshot reader renders,
    /// so scripts stay disabled and remote images stay blocked; only the renderer changes.
    /// </summary>
    private void OpenInBrowserClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Available in every "View as" format: the message's HTML is what the browser shows, whatever the pane is showing now.
        var html = _bodyHtml ?? (string.IsNullOrWhiteSpace(_formatMessage?.BodyHtml) ? null : _formatMessage!.BodyHtml);
        if (html is null) return;
        try
        {
            var path = BrowserDocumentWriter.Write(SafeHtmlDocument.Build(html, _inlineImageBytes));
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            StatusText.Text = "Opened this message in your browser, where text can be selected and copied.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not open the message in a browser: {ex.Message}";
        }
    }

    private void ShowMessageBody()
    {
        RichBodyPanel.Children.Clear();
        BodyViewButton.IsVisible = (_richRuns is not null || _bodyHtml is not null) && !FormatBar.IsVisible;
        ReaderModeButton.IsVisible = _bodyHtml is not null;
        ReaderModeButton.Content = _preferSnapshotForMessage ? "Use interactive reader" : "Use alternate reader";
        ViewOriginalButton.IsVisible = _bodyHtml is not null;
        ViewOriginalButton.Content = _showOriginalHtml ? "View safe layout" : "View original here (trusted mail)";
        PopOutMessageButton.IsVisible = _richRuns is not null || !string.IsNullOrWhiteSpace(_bodyPlain);
        InteractiveReaderButton.IsVisible = _bodyHtml is not null && !_embeddedHtmlActive;
        PrintablePdfButton.IsVisible = PopOutMessageButton.IsVisible;
        OpenInBrowserButton.IsVisible = _bodyHtml is not null || !string.IsNullOrWhiteSpace(_formatMessage?.BodyHtml);
        ReaderZoomControls.IsVisible = _bodyHtml is not null;
        ZoomSlider.IsVisible = _bodyHtml is not null;
        UpdateZoomUi();
        BodyViewButton.Content = _showRichBody ? "View plain text" : "View rich text";
        if (!_showRichBody || (_richRuns is null && _htmlPageBitmaps.Count == 0 && !_embeddedHtmlActive))
        {
            ReaderScrollViewer.IsVisible = true;
            BodyText.IsVisible = true;
            MainHtmlWebView.IsVisible = false;
            RichBodyPanel.IsVisible = false;
            HtmlPagesPanel.IsVisible = false;
            BodyText.Text = _bodyPlain;
            return;
        }
        BodyText.IsVisible = false;
        if (_embeddedHtmlActive)
        {
            ReaderScrollViewer.IsVisible = false;
            MainHtmlWebView.IsVisible = true;
            HtmlPagesPanel.IsVisible = false;
            RichBodyPanel.IsVisible = false;
            return;
        }
        MainHtmlWebView.IsVisible = false;
        ReaderScrollViewer.IsVisible = true;
        if (_htmlPageBitmaps.Count > 0)
        {
            HtmlPagesPanel.IsVisible = true;
            RichBodyPanel.IsVisible = false;
            BodyText.Text = "";
            return;
        }
        HtmlPagesPanel.IsVisible = false;
        RichBodyPanel.IsVisible = true;
        BodyText.Text = "";
        if (_richRuns is null) return;
        TextBlock? line = null;
        var pendingBreaks = 0;
        foreach (var segment in _richRuns)
        {
            if (RichBodyPanel.Children.Count >= 1200)
            {
                RichBodyPanel.Children.Add(new TextBlock { Text = "[Preview shortened]" });
                return;
            }
            if ((segment.ImageContentId is not null || segment.RemoteImageUrl is not null) &&
                _inlineImages.TryGetValue(ImageKey(segment), out var bitmap))
            {
                RichBodyPanel.Children.Add(new Image
                {
                    Source = bitmap, Width = Math.Min(bitmap.PixelSize.Width, 480),
                    Height = Math.Min(bitmap.PixelSize.Height, 360), Stretch = Stretch.Uniform,
                    Margin = new Thickness(0, 6, 0, 6), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
                });
                line = null;
                pendingBreaks = 1;
                continue;
            }
            var parts = segment.Text.Split('\n');
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0) pendingBreaks++;
                if (parts[i].Length == 0) continue;
                if (line is null || pendingBreaks > 0)
                {
                    if (RichBodyPanel.Children.Count >= 1200) return;
                    line = NewLine(pendingBreaks >= 2 ? 9 : pendingBreaks == 1 ? 3 : 0);
                }
                pendingBreaks = 0;
                var run = new Run(parts[i]);
                if (segment.Bold) run.FontWeight = FontWeight.Bold;
                if (segment.Italic) run.FontStyle = FontStyle.Italic;
                if (segment.Underline) run.TextDecorations = TextDecorations.Underline;
                if (segment.Scale != 1) run.FontSize = _appearance.TextSize * segment.Scale;
                line.Inlines?.Add(run);
            }
        }

        TextBlock NewLine(int topMargin = 0)
        {
            var block = new TextBlock { TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, topMargin, 0, 0) };
            RichBodyPanel.Children.Add(block);
            return block;
        }
    }

    private void ClearReader()
    {
        ClearInlineImages();
        _bodyHtml = null;
        _showOriginalHtml = false;
        _htmlImageSources = [];
        _activeMessage = null;
        _activeGraphMessage = null;
        _currentGraphAttachments = null;
        ExportAttachmentButton.IsEnabled = false;
        ExportMessageButton.IsEnabled = false;
        _richRuns = null;
        _bodyPlain = "";
        BodyViewButton.IsVisible = false;
        ReaderModeButton.IsVisible = false;
        OpenInBrowserButton.IsVisible = false;
        HtmlStatusText.IsVisible = false;
        ViewOriginalButton.IsVisible = false;
        PopOutMessageButton.IsVisible = false;
        InteractiveReaderButton.IsVisible = false;
        PrintablePdfButton.IsVisible = false;
        ReaderZoomControls.IsVisible = false;
        RichBodyPanel.Children.Clear();
        RichBodyPanel.IsVisible = false;
        ReaderScrollViewer.IsVisible = true;
        BodyText.IsVisible = true;
        HideFormatBar();
        SubjectText.Text = "Select a message";
        SenderText.Text = RecipientText.Text = AttachmentText.Text = BodyText.Text = "";
        ShowAttachmentChips(null);
        MessageDateText.Text = "";
        ReaderAvatar.IsVisible = false;
        ReaderReplyButton.IsVisible = ReaderReplyAllButton.IsVisible =
            ReaderForwardButton.IsVisible = false;
    }

    private sealed record FolderSelection(string Path, MailFolder Folder);
    private sealed record MicrosoftFolderSelection(ConnectedAccount Account, string Id, string Name);
}
