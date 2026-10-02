using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PstCore;

namespace OpenOutlook.Desktop;

/// <summary>
/// Editing mode for PST archives. Off (the default), archives stay exactly what they have always
/// been: opened read-only, with local read-state kept in the sidecar overlay. Turning it on opens a
/// PstEditSession - a byte-for-byte .bak is made first, the archive is reopened writable, and every
/// flag edit lands in the real file (both the item property and the folder contents-table copy, plus
/// the unread badge). Turning it off runs a full integrity verification; if anything fails the
/// original archive is restored from the backup automatically. Window close finalizes open sessions
/// the same way, so edits are never silently dropped or half-written.
/// </summary>
public partial class MainWindow
{
    private readonly Dictionary<string, PstEditSession> _editSessions = new(StringComparer.Ordinal);

    private void ToggleArchiveEditing(string archivePath)
    {
        if (_editSessions.TryGetValue(archivePath, out var session))
        {
            try
            {
                session.Commit();
                StatusText.Text = $"Editing finished · {Path.GetFileName(archivePath)} verified against every block CRC · backup kept at {Path.GetFileName(session.BackupPath)}";
            }
            catch (Exception ex) when (ex is PstException or IOException or UnauthorizedAccessException)
            {
                session.Rollback();
                AppLog.Error("pst-edit", ex, "commit failed; archive restored from backup");
                StatusText.Text = "Archive failed verification after editing - the original was restored from backup.";
            }
            finally
            {
                _editSessions.Remove(archivePath);
                session.Dispose(); // idempotent after Commit/Rollback
                ReplaceWithReadOnlyStore(archivePath);
            }
            _ = RefreshActivePstFolderAsync(archivePath);
            return;
        }

        if (!_stores.TryGetValue(archivePath, out var current))
        {
            StatusText.Text = "That archive is not open.";
            return;
        }
        try
        {
            var begun = PstEditSession.Begin(archivePath);
            _editSessions[archivePath] = begun;
            // The read-only store may still back in-flight reader tasks; leave it open (the process
            // closes it at shutdown) and hand the writable one to everything that looks it up anew.
            _stores[archivePath] = begun.Store;
            InvalidateFolderCache(archivePath);
            StatusText.Text = $"EDITING {Path.GetFileName(archivePath)} · flag changes write to the archive immediately · backup: {Path.GetFileName(begun.BackupPath)}";
        }
        catch (Exception ex) when (ex is PstException or IOException or UnauthorizedAccessException)
        {
            AppLog.Error("pst-edit", ex, "could not begin editing session");
            StatusText.Text = $"Could not start editing: {ex.Message}";
        }
        _ = RefreshActivePstFolderAsync(archivePath);
    }

    private void ReplaceWithReadOnlyStore(string archivePath)
    {
        if (_stores.TryGetValue(archivePath, out var writable))
        {
            try { writable.Dispose(); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { /* closing is best effort */ }
        }
        try
        {
            var fresh = PstStore.Open(archivePath, writable: false);
            _stores[archivePath] = fresh;
            InvalidateFolderCache(archivePath);
        }
        catch (Exception ex) when (ex is PstException or IOException or UnauthorizedAccessException)
        {
            AppLog.Error("pst-edit", ex, "could not reopen archive read-only after editing");
            StatusText.Text = $"Editing ended but the archive could not be reopened: {ex.Message}";
        }
    }

    private void InvalidateFolderCache(string archivePath)
    {
        foreach (var key in _folderCache.Keys.Where(k => k.Path == archivePath).ToList())
            _folderCache.Remove(key);
    }

    /// <summary>Re-renders the visible folder from the store that is current for its path, using the
    /// same gate/version discipline as a fresh folder selection.</summary>
    private async Task RefreshActivePstFolderAsync(string archivePath)
    {
        if (_activePath != archivePath || _activeFolder is not { } folder) return;
        if (!_stores.TryGetValue(archivePath, out var store)) return;
        var version = Interlocked.Increment(ref _folderVersion);
        try
        {
            await _readerGate.WaitAsync();
            IReadOnlyList<MailSummary> messages;
            try
            {
                if (version != _folderVersion) return;
                messages = await Task.Run(() => store.GetMessages(folder));
            }
            finally { _readerGate.Release(); }
            if (version != _folderVersion) return;
            CacheFolder((archivePath, folder.Nid), messages);
            ShowMessages(messages);
        }
        catch (Exception ex) when (ex is PstException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            if (version == _folderVersion) StatusText.Text = $"Could not read folder: {ex.Message}";
        }
    }

    /// <summary>
    /// Handles read/unread/flag actions for a selected PST message before the Microsoft-account logic.
    /// Returns false when the selection is not a PST row or the action is not a flag action, so the
    /// caller continues unchanged. Writable archives (editing mode) write into the file and drop the
    /// sidecar override; read-only archives keep using the overlay exactly as before.
    /// </summary>
    private bool TryHandlePstFlagAction(string action)
    {
        if (action is not ("read" or "unread" or "flag" or "unflag")) return false;
        if (MessageList.SelectedItem is not MessageListRow row) return false;
        if (_activePath is not { } path || !_stores.TryGetValue(path, out var store)) return false;

        bool read = action is "read" or "unread";
        bool on = action is "read" or "flag";
        try
        {
            if (read)
            {
                if (store.CanWrite && row.Summary.IsRead != on) store.SetReadState(row.Summary, on);
                row.SetRead(on);
            }
            else
            {
                store.SetFlagged(row.Summary, on); // throws when the item carries no flag property (read-only or otherwise)
                row.Summary.Flagged = on;
            }
        }
        catch (PstException ex)
        {
            StatusText.Text = read
                ? $"Could not change read state: {ex.Message}"
                : $"Could not change the flag: {ex.Message}";
            return true; // handled - do not fall through to the mailbox path
        }

        var key = PstMessageKey(path, row.Summary.Nid);
        if (read)
        {
            if (store.CanWrite) _readOverrides.Remove(key); // native flag now authoritative
            else _readOverrides[key] = on;
            PersistReadState();
        }
        var what = read ? "Read state" : "Flag";
        StatusText.Text = store.CanWrite
            ? $"{what} written to {Path.GetFileName(path)} · turn off Editing Mode to verify and seal the archive"
            : $"{what} saved locally (archive is opened read-only)";
        return true;
    }

    /// <summary>Called from the shutdown path: an open editing session is committed with verification,
    /// or rolled back when verification fails - never left half-written.</summary>
    internal void FinalizeEditSessionsForShutdown()
    {
        foreach (var (path, session) in _editSessions.ToList())
        {
            try
            {
                session.Commit();
            }
            catch (Exception ex) when (ex is PstException or IOException or UnauthorizedAccessException)
            {
                AppLog.Error("pst-edit", ex, "shutdown commit failed; archive restored from backup");
                session.Rollback();
            }
            finally
            {
                _editSessions.Remove(path);
                session.Dispose();
            }
        }
    }
}
