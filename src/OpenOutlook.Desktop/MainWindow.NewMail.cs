using Avalonia.Threading;
using OpenOutlook.Auth;
using OpenOutlook.Mirror;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Desktop;

/// <summary>
/// Outlook's scheduled send/receive: every minute by default (Options > Advanced > Send and receive) each connected account is asked a few cheap
/// questions - Graph: the Inbox's (and the open folder's) counts and newest message; Gmail: the mailbox history id, then the label counts when it moved.
/// The unread numbers in the folder list follow at once; only when the Inbox or the open folder changed is the mailbox copy synchronised, which reloads
/// the open list in place (the selection stays). Send/Receive All Folders runs the same check for every account at once and always synchronises.
/// The 15-minute full sync (<see cref="StartMirrorScheduler"/>) still catches everything else (other folders, flags changed elsewhere).
/// </summary>
public partial class MainWindow
{
    private readonly NewMailTracker _newMail = new();
    private readonly Dictionary<string, GraphNewMailProbe> _newMailProbes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _newMailLastError = new(StringComparer.Ordinal);   // logged once per account until a check works again
    private DispatcherTimer? _newMailTimer;
    private bool _newMailBusy;

    private void StartNewMailScheduler()
    {
        StartNewMailBadge();
        AddHandler(KeyDownEvent, (_, e) =>                                                 // F9, as in Outlook: Send/Receive All Folders
        {
            if (e.Key != Avalonia.Input.Key.F9 || e.KeyModifiers != Avalonia.Input.KeyModifiers.None || !QuickSendReceiveButton.IsEnabled) return;
            SendReceiveAllClicked(this, new Avalonia.Interactivity.RoutedEventArgs());
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Bubble);
        if (Environment.GetEnvironmentVariable("OPENOUTLOOK_NO_MIRROR") == "1") return;     // tests and tools that must not touch real mailboxes
        // the first check comes before the start-up sync of the copies (20 s): its baseline is then older than that sync, so nothing is missed
        _newMailTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _newMailTimer.Tick += async (_, _) =>
        {
            _newMailTimer!.Interval = NewMailTracker.Interval(_options.NewMailCheckMinutes);
            if (_options.NewMailCheckEnabled) await CheckForNewMailAsync(manual: false);
        };
        _newMailTimer.Start();
    }

    /// <summary>Options saved: the next check follows the new interval.</summary>
    private void NewMailOptionsChanged()
    {
        if (_newMailTimer is null) return;
        _newMailTimer.Stop();
        _newMailTimer.Interval = NewMailTracker.Interval(_options.NewMailCheckMinutes);
        _newMailTimer.Start();
    }

    /// <summary>Checks every connected account in turn. Never throws: a failed check (offline, sign-in expired) is logged once and tried again next time.</summary>
    internal async Task CheckForNewMailAsync(bool manual)
    {
        if (_newMailBusy)
        {
            if (!manual) return;
            for (var wait = 0; _newMailBusy && wait < 120; wait++) await Task.Delay(500);   // Send/Receive All waits for a scheduled check in progress
            if (_newMailBusy) return;
        }
        _newMailBusy = true;
        try
        {
            foreach (var account in MirrorAccounts())
            {
                try
                {
                    if (account.Provider == OAuthProvider.Google) await CheckGmailForNewMailAsync(account, manual);
                    else await CheckMicrosoftForNewMailAsync(account, manual);
                    _newMailLastError.Remove(account.AccountId);
                }
                catch (Exception e)
                {
                    if (!_newMailLastError.TryGetValue(account.AccountId, out var last) || last != e.Message)
                        AppLog.Error("new-mail", e, $"checking {account.DisplayAddress} for new mail failed; trying again at the next check");
                    _newMailLastError[account.AccountId] = e.Message;
                    if (manual) await CatchUpAccountAsync(account);                          // Send/Receive All still runs the sync, which reports the problem
                }
            }
        }
        finally { _newMailBusy = false; }
    }

    private bool CopySyncedOrSyncing(ConnectedAccount account) => _mirrorLastSync.ContainsKey(account.AccountId) || _mirrorBusy;

    private async Task CheckMicrosoftForNewMailAsync(ConnectedAccount account, bool manual)
    {
        var synced = CopySyncedOrSyncing(account);
        var open = _activeMicrosoftFolder is { } folder && folder.Account.AccountId == account.AccountId && folder.Id != "inbox" ? folder : null;
        var token = await GetMicrosoftSession(account).GetAccessTokenAsync(CancellationToken.None);
        if (!_newMailProbes.TryGetValue(account.AccountId, out var probe))
            _newMailProbes[account.AccountId] = probe = new GraphNewMailProbe(_graphHttp, account.AccountId);
        var probes = await probe.ProbeAsync(token, open is null ? ["inbox"] : ["inbox", open.Id]);
        ApplyFolderCounts(account, probes.ToDictionary(p => p.FolderId, p => p.UnreadCount, StringComparer.Ordinal));     // "Inbox (1)" at once
        NoteInboxUnread(account.AccountId, probes[0].UnreadCount);
        var inbox = _newMail.Observe(account.AccountId, "inbox", probes[0].Fingerprint);
        NewMailVerdict? openVerdict = open is null ? null : _newMail.Observe(account.AccountId, open.Id, probes[1].Fingerprint);
        if (!NewMailTracker.ShouldSync(inbox, openVerdict, synced, manual)) return;
        if (!manual) AppLog.Note("new-mail", $"{account.DisplayAddress}: {(inbox == NewMailVerdict.Changed ? "the Inbox" : open?.Name ?? "the Inbox")} changed (Inbox {probes[0].UnreadCount} unread of {probes[0].TotalCount}); updating the copy");
        await CatchUpAccountAsync(account);
    }

    private async Task CheckGmailForNewMailAsync(ConnectedAccount account, bool manual)
    {
        var synced = CopySyncedOrSyncing(account);
        var box = GetGmailMailbox(account);
        var history = _newMail.Observe(account.AccountId, "history", await box.GetHistoryIdAsync());
        if (history == NewMailVerdict.Same && !manual) return;                                // nothing at all changed in the mailbox: one request per check
        var open = _activeGmailFolder is { } folder && folder.Account.AccountId == account.AccountId && folder.LabelId != "INBOX" ? folder : null;
        var (total, unread) = await box.GetLabelCountsAsync("INBOX");
        SetGmailUnread(account, "INBOX", unread);
        NoteInboxUnread(account.AccountId, unread);
        var inbox = _newMail.Observe(account.AccountId, "INBOX", $"{total}/{unread}");
        NewMailVerdict? openVerdict = null;
        if (open is not null)
        {
            var (openTotal, openUnread) = await box.GetLabelCountsAsync(open.LabelId);
            SetGmailUnread(account, open.LabelId, openUnread);
            openVerdict = _newMail.Observe(account.AccountId, open.LabelId, $"{openTotal}/{openUnread}");
        }
        if (!NewMailTracker.ShouldSync(inbox, openVerdict, synced, manual)) return;
        if (!manual) AppLog.Note("new-mail", $"{account.DisplayAddress}: {(inbox == NewMailVerdict.Changed ? "the Inbox" : open?.Name ?? "the Inbox")} changed (Inbox {unread} unread of {total}); updating the copy");
        await CatchUpAccountAsync(account);
    }

    /// <summary>Something changed on the server: every folder's unread number is read again, and the copy is synchronised (which reloads the open
    /// list when it brought anything). Without a copy the open list is read from the server again.</summary>
    private async Task CatchUpAccountAsync(ConnectedAccount account)
    {
        if (account.Provider == OAuthProvider.Google) _ = RefreshGmailCountsAsync(account);
        else _ = RefreshMicrosoftFolderCountsAsync(account, CancellationToken.None, force: true);
        if (Environment.GetEnvironmentVariable("OPENOUTLOOK_NO_MIRROR") != "1" && _mirrorSettings.Load().For(account.AccountId).Enabled)     // tests: no mailbox copies
        {
            await SyncMirrorWhenIdleAsync(account, manual: false);
            if (!_mirrorInfo.TryGetValue(account.AccountId, out var info) || info.Phase is SyncPhase.Offline or SyncPhase.Problem or SyncPhase.Syncing)
                _newMail.Invalidate(account.AccountId);                                    // not brought up to date: the next check tries again
            return;
        }
        if (_activeMicrosoftFolder?.Account.AccountId == account.AccountId) await RefreshMicrosoftFolderAsync();
        else if (_activeGmailFolder is { } gmail && gmail.Account.AccountId == account.AccountId) await ReloadGmailFolderAsync(gmail);
    }

    /// <summary>Sets one Gmail folder's unread number in the folder list.</summary>
    private void SetGmailUnread(ConnectedAccount account, string labelId, int unread)
    {
        _gmailUnread[account.AccountId + "|" + labelId] = unread;
        void Walk(IEnumerable<object?> items)
        {
            foreach (var node in items.OfType<Avalonia.Controls.TreeViewItem>())
            {
                if (node.Tag is GmailFolderSelection s && s.Account.AccountId == account.AccountId && s.LabelId == labelId) node.Header = FolderHeader(s.Name, unread);
                Walk(node.Items.Cast<object?>());
            }
        }
        Walk(FolderTree.Items.Cast<object?>());
    }
}
