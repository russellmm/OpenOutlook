using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenOutlook.PstNative;
using PstCore;

namespace OpenOutlook.Desktop;

/// <summary>
/// PST archives are editable at all times, like Outlook: archives open writable through the native OpenPST
/// engine (always-edit mode), so there is no "start editing" step. Every operation is one atomic, journaled
/// native transaction - a crash mid-write is rolled back the next time the archive opens. An archive that cannot
/// be opened for writing (locked by another window, read-only file or media) stays readable and says why; its
/// read state then falls back to the local sidecar overlay. A byte-for-byte .bak copy before editing is opt-in
/// (OptionsSettings.PstBackupBeforeEditing, default off).
/// </summary>
public partial class MainWindow
{
    /// <summary>Why an archive is open read-only (key = archive path). Absent for editable archives.</summary>
    private readonly Dictionary<string, string> _readOnlyReasons = new(StringComparer.Ordinal);

    /// <summary>Returns the editable store for the archive, or null when it is open read-only; the reason is
    /// already in the status bar.</summary>
    private IPstEngine? EnsureWritableStore(string archivePath)
    {
        if (_stores.TryGetValue(archivePath, out var store) && store.CanWrite) return store;
        var why = _readOnlyReasons.TryGetValue(archivePath, out var r) ? r : "it was opened read-only";
        StatusText.Text = $"{Path.GetFileName(archivePath)} cannot be edited: {why}";
        return null;
    }

    /// <summary>One-time byte-for-byte copy next to the archive, only when the user opted in.</summary>
    private void BackupBeforeEditingIfRequested(string archivePath)
    {
        if (!_options.PstBackupBeforeEditing) return;
        try { File.Copy(archivePath, archivePath + ".bak", overwrite: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { AppLog.Error("pst-edit", ex, "could not make the optional .bak copy"); }
    }

    /// <summary>Kept for the call sites that ran verify-or-rollback after each edit. Native edits are atomic and
    /// journaled, so there is nothing to verify or roll back per operation; this only reports whether the archive
    /// is still open for editing.</summary>
    private Task<bool> VerifyOperationAsync(string archivePath) =>
        Task.FromResult(_stores.TryGetValue(archivePath, out var store) && store.CanWrite);

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
    private async Task<bool> TryHandlePstFlagActionAsync(string action)
    {
        if (action is not ("read" or "unread" or "flag" or "unflag")) return false;
        if (MessageList.SelectedItem is not MessageListRow row) return false;
        if (_activePath is not { } path) return false;

        bool read = action is "read" or "unread";
        bool on = action is "read" or "flag";
        var store = EnsureWritableStore(path);
        if (store is null) return true;
        try
        {
            if (read)
            {
                if (row.Summary.IsRead != on) await Task.Run(() => store.SetReadState(row.Summary, on));
                row.SetRead(on);
            }
            else
            {
                await Task.Run(() => store.SetFlagged(row.Summary, on));
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
            _readOverrides.Remove(key); // native flag is authoritative now
            PersistReadState();
        }
        if (!await VerifyOperationAsync(path)) return true;
        var what = read ? "Read state" : "Flag";
        StatusText.Text = $"{what} saved to {Path.GetFileName(path)} \u00b7 saved";
        return true;
    }

    /// <summary>Handles Delete for PST rows    /// <summary>Handles Delete for PST rows before the Microsoft-account logic. Read-only archives get
    /// a pointer to Editing Mode instead of the mailbox popup; writable archives confirm and then
    /// unlink the rows for real (Phase B delete). The list is reloaded from the file afterwards so
    /// what the user sees is exactly what the archive now contains.</summary>
    private async Task<bool> TryHandlePstDeleteAsync(string action, bool permanent = false)
    {
        if (action != "delete") return false;
        var rows = MessageList.SelectedItems.OfType<MessageListRow>()
            .Concat(MessageList.SelectedItem is MessageListRow single ? [single] : Array.Empty<MessageListRow>())
            .Distinct().ToList();
        if (rows.Count == 0) return false;
        if (_activePath is not { } path)
        {
            StatusText.Text = "The archive for this selection is no longer open.";
            return true;
        }
        var store = EnsureWritableStore(path);
        if (store is null) return true;
        // Classic Outlook semantics: Delete moves to Deleted Items; Shift+Delete (or deleting from
        // inside Deleted Items) removes for real. Both paths confirm first.
        var inDeletedItems = _activeFolder is not null &&
            string.Equals(_activeFolder.Name, "Deleted Items", StringComparison.OrdinalIgnoreCase);
        if (!permanent && !inDeletedItems)
        {
            var trash = store.AllFolders().FirstOrDefault(f =>
                string.Equals(f.Name, "Deleted Items", StringComparison.OrdinalIgnoreCase));
            if (trash is null)
            {
                StatusText.Text = "This archive has no Deleted Items folder - use Shift+Delete to remove messages permanently.";
                return true;
            }
            var moved = 0;
            string? moveError = null;
            foreach (var row in rows)
            {
                try { store.MoveMessage(row.Summary, trash); moved++; }
                catch (Exception ex) when (ex is PstException or IOException) { moveError ??= ex.Message; }
            }
            _ = RefreshActivePstFolderAsync(path);
            if (moved > 0 && !await VerifyOperationAsync(path)) return true;
            StatusText.Text = moved > 0
                ? $"Moved {moved} message{(moved == 1 ? "" : "s")} to Deleted Items in {Path.GetFileName(path)}" +
                  (moveError is null || moved == rows.Count ? " \u00b7 saved" : $" \u00b7 {rows.Count - moved} not moved: {moveError}")
                : $"Could not move to Deleted Items: {moveError ?? "unknown reason"}";
            return true;
        }
        if (!await ConfirmPstDeleteAsync(rows.Count, Path.GetFileName(path))) return true;
        var done = 0;
        string? firstError = null;
        foreach (var row in rows)
        {
            try { store.DeleteMessage(row.Summary); done++; }
            catch (Exception ex) when (ex is PstException or IOException) { firstError ??= ex.Message; }
        }
        _ = RefreshActivePstFolderAsync(path);
        if (done > 0 && !await VerifyOperationAsync(path)) return true;
        StatusText.Text = done > 0
            ? $"Deleted {done} message{(done == 1 ? "" : "s")} from {Path.GetFileName(path)}" +
              (firstError is null || done == rows.Count ? " \u00b7 saved" : $" \u00b7 {rows.Count - done} not deleted: {firstError}")
            : $"Could not delete: {firstError ?? "unknown reason"}";
        return true;
    }

    private async Task<bool> ConfirmPstDeleteAsync(int count, string archiveName)
    {
        var dialog = new Window
        {
            Title = count == 1 ? "Delete message from archive?" : "Delete messages from archive?",
            Width = 460, Height = 200, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var delete = new Button { Content = "Delete from archive" };
        var cancel = new Button { Content = "Cancel" };
        delete.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 14, Children =
            {
                new TextBlock
                {
                    Text = $"Remove {(count == 1 ? "this message" : $"{count} messages")} from {archiveName}? This cannot be undone.",
                    TextWrapping = TextWrapping.Wrap
                },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { delete, cancel } }
            }
        };
        return await dialog.ShowDialog<bool>(this);
    }

    /// <summary>Purge: empties a writable archive's Deleted Items folder - every message in it goes
    /// through the same validated Phase B delete. Runs under the reader gate so no read can observe
    /// the folder half-emptied, and only after an explicit confirmation.</summary>
    private async Task EmptyDeletedItemsAsync(FolderSelection sel)
    {
        var store = EnsureWritableStore(sel.Path);
        if (store is null) return;
        int done = 0;
        string? firstError = null;
        try
        {
            await _readerGate.WaitAsync();
            try
            {
                var messages = await Task.Run(() => store.GetMessages(sel.Folder));
                if (messages.Count == 0)
                {
                    StatusText.Text = "Deleted Items is already empty.";
                    return;
                }
                if (!await ConfirmPstDeleteAsync(messages.Count, Path.GetFileName(sel.Path))) return;
                foreach (var message in messages)
                {
                    try { store.DeleteMessage(message); done++; }
                    catch (Exception ex) when (ex is PstException or IOException) { firstError ??= ex.Message; }
                }
            }
            finally { _readerGate.Release(); }
        }
        catch (Exception ex) when (ex is PstException or IOException or ObjectDisposedException)
        {
            StatusText.Text = $"Could not empty Deleted Items: {ex.Message}";
            return;
        }
        if (_activePath == sel.Path && _activeFolder?.Nid == sel.Folder.Nid)
            _ = RefreshActivePstFolderAsync(sel.Path);
        if (done > 0 && !await VerifyOperationAsync(sel.Path)) return;
        StatusText.Text = done > 0
            ? $"Emptied {done} message{(done == 1 ? "" : "s")} from Deleted Items in {Path.GetFileName(sel.Path)}" +
              (firstError is null ? " \u00b7 saved" : $" \u00b7 some could not be removed: {firstError}")
            : $"Could not empty Deleted Items: {firstError ?? "unknown reason"}";
    }
}
