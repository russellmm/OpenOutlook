using System;
using System.Collections.Generic;
using Avalonia.Threading;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Desktop;

/// <summary>
/// Reading Pane read-tracking, driven by the Options dialog values (File > Options > Mail >
/// Outlook panes > Reading Pane): "Mark items as read when viewed in the Reading Pane" starts a
/// timer of N seconds on every selected message and marks it read when the timer elapses;
/// "Mark item as read when selection changes" marks the previous item read the moment another one
/// is selected. Because PST archives are opened read-only, the new state is remembered in
/// read-state.json (see ReadStateStore) and re-applied whenever the lists are rebuilt; mailbox
/// messages carry their real flags on top of the same overlay.
/// </summary>
public partial class MainWindow
{
    private readonly ReadStateStore _readStateStore = new();
    private Dictionary<string, bool> _readOverrides = new(StringComparer.Ordinal);
    private bool _readOverridesLoaded;
    private DispatcherTimer? _markReadTimer;
    private string? _readingKey;
    private Action<bool>? _readingApply;
    private Action? _readingNativePersist;

    public static string PstMessageKey(string archivePath, uint nid) =>
        "pst:" + archivePath + "|" + nid.ToString("X8");

    public static string GraphMessageKey(string accountId, string messageId) =>
        "msg:" + accountId + "/" + messageId;

    private void InitializeReadingState() => _options = _optionsStore.Load();

    private bool? OverrideFor(string key)
    {
        if (!_readOverridesLoaded)
        {
            _readOverrides = _readStateStore.Load();
            _readOverridesLoaded = true;
        }
        return _readOverrides.TryGetValue(key, out var forced) ? forced : null;
    }

    /// <summary>Applies the stored read state to freshly fetched mailbox messages so a local
    /// mark-read survives the next server refresh until real flag writes land.</summary>
    private GraphInboxMessage WithLocalReadState(GraphInboxMessage message) =>
        _activeMicrosoftAccount is { } account &&
        OverrideFor(GraphMessageKey(account.AccountId, message.Id)) is { } forced && forced != message.IsRead
            ? message with { IsRead = forced }
            : message;

    /// <summary>Called on every message selection (and on list rebuilds): closes out the previously
    /// tracked item and starts the mark-as-read timer for the newly selected one.</summary>
    private void TrackReadingPaneItem()
    {
        var previousKey = _readingKey;
        var previousApply = _readingApply;
        var previousNative = _readingNativePersist;
        _readingKey = null;
        _readingApply = null;
        _readingNativePersist = null;
        _markReadTimer?.Stop();

        if (previousKey is not null && previousApply is not null && _options.ReadPaneMarkOnSelectionChange)
            MarkItemRead(previousKey, previousApply, previousNative);

        string? key = null;
        Action<bool>? apply = null;
        Action? nativePersist = null;
        bool alreadyRead = false;
        if (MessageList.SelectedItem is MessageListRow pstRow && _activePath is { } archivePath)
        {
            key = PstMessageKey(archivePath, pstRow.Summary.Nid);
            alreadyRead = pstRow.IsRead;
            apply = read => pstRow.SetRead(read);
            if (_stores.TryGetValue(archivePath, out var store) && store.CanWrite)
                nativePersist = () => store.SetReadState(pstRow.Summary, true);
        }
        else if (MessageList.SelectedItem is GraphMessageListRow graphRow && _activeMicrosoftAccount is { } account)
        {
            key = GraphMessageKey(account.AccountId, graphRow.Message.Id);
            alreadyRead = graphRow.Message.IsRead;
            apply = read => graphRow.Update(graphRow.Message with { IsRead = read });
            if (account.CanWriteMicrosoftMail)            // opening a message marks it read in the mailbox itself, so other clients (Outlook) see it
            {
                var messageId = graphRow.Message.Id;
                var writer = new GraphMailWriter(_graphHttp, account.AccountId);
                nativePersist = () => writer.SetReadAsync(GetMicrosoftSession(account).GetAccessTokenAsync().GetAwaiter().GetResult(), messageId, true).GetAwaiter().GetResult();
            }
        }
        if (key is null || apply is null) return;

        _readingKey = key;
        _readingApply = apply;
        _readingNativePersist = nativePersist;
        if (alreadyRead || !_options.ReadPaneMarkOnView) return;

        var seconds = Math.Clamp(_options.ReadPaneWaitSeconds, 0, 300);
        if (seconds == 0) { MarkItemRead(key, apply, nativePersist); return; }
        _markReadTimer ??= CreateMarkReadTimer();
        _markReadTimer.Interval = TimeSpan.FromSeconds(seconds);
        _markReadTimer.Start();
    }

    private DispatcherTimer CreateMarkReadTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_readingKey is { } key && _readingApply is { } apply) MarkItemRead(key, apply, _readingNativePersist);
        };
        return timer;
    }

    private void MarkItemRead(string key, Action<bool> apply, Action? nativePersist = null)
    {
        _readingApply = null;
        _readingKey = null;
        _readingNativePersist = null;
        if (OverrideFor(key) == true) return;
        try { apply(true); }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
        { /* The row's store closed underneath us; the persisted override below still stands. */ }
        // Editing mode: write the flag into the archive itself and drop the sidecar override so the
        // native value is authoritative from now on. Any failure falls back to the overlay silently -
        // the user-visible state is already correct either way.
        if (nativePersist is not null)
        {
            _ = PersistNativeReadAsync(key, nativePersist);
            return;
        }
        _readOverrides[key] = true;
        PersistReadState();
    }

    /// <summary>Writes the read flag into the archive off the UI thread (a native write rewrites the folder's contents table,
    /// which takes a few hundred milliseconds on big folders). Any failure falls back to the sidecar overlay.</summary>
    private async Task PersistNativeReadAsync(string key, Action persist)
    {
        var mailbox = MessageList.SelectedItem is GraphMessageListRow ? _activeMicrosoftAccount : null;
        try
        {
            await Task.Run(persist);
            _readOverrides.Remove(key);
            PersistReadState();
            if (mailbox is not null) await RefreshMicrosoftFolderCountsAsync(mailbox, CancellationToken.None, force: true);    // the folder's unread number follows
            return;
        }
        catch (Exception exception) when (exception is PstCore.PstException or IOException or ObjectDisposedException or InvalidOperationException or GraphMailException or System.Net.Http.HttpRequestException)
        { AppLog.Error("pst-edit", exception, "mark-read could not be written to the archive or mailbox; using local overlay instead"); }
        _readOverrides[key] = true;
        PersistReadState();
    }

    private void PersistReadState()
    {
        try { _readStateStore.Save(_readOverrides); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { AppLog.Error("read-state", exception, "could not persist read state"); }
    }
}
