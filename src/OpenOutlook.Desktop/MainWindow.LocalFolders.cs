using Avalonia.Threading;
using OpenOutlook.Auth;
using OpenOutlook.Mirror;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Desktop;

/// <summary>Folders of a connected Microsoft account are shown from its local mailbox copy when there is one, as Outlook does; the server is asked only to keep that copy current.</summary>
public partial class MainWindow
{
    private readonly HashSet<string> _localRemoved = new(StringComparer.Ordinal);                  // accountId|messageId deleted or moved here, not yet in the copy
    private readonly Dictionary<string, bool> _localFlags = new(StringComparer.Ordinal);           // accountId|messageId -> flag changed here, not yet in the copy
    private readonly Dictionary<string, bool> _localReads = new(StringComparer.Ordinal);           // accountId|messageId -> read state changed here, not yet in the copy
    private DispatcherTimer? _mirrorSoonTimer;
    private ConnectedAccount? _mirrorSoonAccount;

    /// <summary>The folder from the local copy, or null when the copy cannot answer (turned off, never synchronised, not open): the caller then asks the server.</summary>
    private async Task<GraphInboxPage?> ReadLocalFolderAsync(ConnectedAccount account, string folderId)
    {
        try
        {
            if (LocalCopyOf(account) is not var (store, statePath)) return null;
            var prefix = account.AccountId + "|";
            var hidden = _localRemoved.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Select(k => k[prefix.Length..]).ToHashSet(StringComparer.Ordinal);
            var flags = _localFlags.Where(k => k.Key.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary(k => k.Key[prefix.Length..], k => k.Value, StringComparer.Ordinal);
            await _readerGate.WaitAsync();
            try
            {
                var page = await Task.Run(() =>
                {
                    using var state = new SyncStateStore(statePath);
                    return LocalMailboxReader.Read(store, state, folderId, hidden, flags);
                });
                if (page is null) return null;
                var reads = _localReads.Where(k => k.Key.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary(k => k.Key[prefix.Length..], k => k.Value, StringComparer.Ordinal);
                if (reads.Count == 0) return page;
                var messages = page.Messages.Select(m => reads.TryGetValue(m.Id, out var read) ? m with { IsRead = read } : m).ToList();
                return page with { Messages = messages, UnreadCount = messages.Count(m => !m.IsRead) };
            }
            finally { _readerGate.Release(); }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException or PstCore.PstException or Microsoft.Data.Sqlite.SqliteException)
        {
            AppLog.Error("local-folder", e, "the local copy could not be read; asking the server");
            return null;
        }
    }

    /// <summary>The open local copy of an account and the path of its sync state, or null when mirroring is off or the copy is not open.</summary>
    private (PstCore.IPstEngine Store, string StatePath)? LocalCopyOf(ConnectedAccount account)
    {
        var settings = _mirrorSettings.Load();
        if (!settings.For(account.AccountId).Enabled) return null;
        var path = MirrorLocations.PstPathFor(settings, account.AccountId, account.DisplayAddress);
        var statePath = System.IO.Path.ChangeExtension(path, ".sync");
        return File.Exists(statePath) && _stores.TryGetValue(path, out var store) ? (store, statePath) : null;
    }

    /// <summary>The body of a message from the local copy (no network request), or null when the copy does not hold it.</summary>
    private async Task<LocalBody?> ReadLocalBodyAsync(ConnectedAccount account, string messageId)
    {
        try
        {
            if (LocalCopyOf(account) is not var (store, statePath)) return null;
            await _readerGate.WaitAsync();
            try
            {
                return await Task.Run(() =>
                {
                    using var state = new SyncStateStore(statePath);
                    return LocalMailboxReader.ReadBody(store, state, messageId);
                });
            }
            finally { _readerGate.Release(); }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException or PstCore.PstException or Microsoft.Data.Sqlite.SqliteException)
        { return null; }
    }

    /// <summary>A change made here that the server accepted: remembered until the next sync brings the copy up to date, so the list is right at once.</summary>
    private void NoteLocalChange(ConnectedAccount account, string messageId, string action)
    {
        var key = account.AccountId + "|" + messageId;
        if (action is "delete" or "archive") _localRemoved.Add(key);
        else if (action is "flag" or "unflag") _localFlags[key] = action == "flag";
        else if (action is "read" or "unread") _localReads[key] = action == "read";
        RequestMirrorSyncSoon(account);
    }

    /// <summary>The copy catches up a few seconds after the last change (many changes in a row make one sync).</summary>
    private void RequestMirrorSyncSoon(ConnectedAccount account)
    {
        if (Environment.GetEnvironmentVariable("OPENOUTLOOK_NO_MIRROR") == "1") return;
        _mirrorSoonAccount = account;
        _mirrorSoonTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _mirrorSoonTimer.Tick -= MirrorSoonTick;
        _mirrorSoonTimer.Tick += MirrorSoonTick;
        _mirrorSoonTimer.Stop();
        _mirrorSoonTimer.Start();
    }

    private async void MirrorSoonTick(object? sender, EventArgs e)
    {
        _mirrorSoonTimer?.Stop();
        if (_mirrorSoonAccount is { } account) await SyncMirrorAsync(account, manual: false);
    }

    /// <summary>After a sync the copy has caught up with this computer's changes; the list is re-read when it is the one on screen.</summary>
    private void AfterMirrorSyncLocal(ConnectedAccount account, bool changed)
    {
        var prefix = account.AccountId + "|";
        _localRemoved.RemoveWhere(k => k.StartsWith(prefix, StringComparison.Ordinal));
        foreach (var k in _localReads.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList()) _localReads.Remove(k);
        foreach (var k in _localFlags.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList()) _localFlags.Remove(k);
        if (!changed) return;
        if (_activeMicrosoftFolder?.Account.AccountId == account.AccountId) _ = RefreshMicrosoftFolderAsync();
        else if (_activeGmailFolder is { } gmail && gmail.Account.AccountId == account.AccountId) _ = ReloadGmailFolderAsync(gmail);
    }
}
