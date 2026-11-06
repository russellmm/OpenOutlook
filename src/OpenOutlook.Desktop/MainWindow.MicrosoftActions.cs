using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using OpenOutlook.Auth;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Desktop;

/// <summary>Mark read / unread, flag, archive and delete for Microsoft mail: the list changes at once and Microsoft is told in the background.</summary>
public partial class MainWindow
{
    private readonly SemaphoreSlim _msActionGate = new(1, 1);

    /// <summary>True for an action that can change the screen first (a delete inside Deleted Items asks for confirmation and removes mail for good, so it waits for Microsoft).</summary>
    private static bool CanActAtOnce(string action, MicrosoftFolderSelection folder) =>
        action is "read" or "unread" or "flag" or "unflag" or "archive" ||
        (action == "delete" && !folder.Name.Equals("Deleted Items", StringComparison.OrdinalIgnoreCase));

    /// <summary>Applies the action to the list now, then sends it (batched, 20 messages per request). A refusal puts those messages back and says why.</summary>
    private void ActAtOnce(ConnectedAccount account, MicrosoftFolderSelection folder, string action, IReadOnlyList<GraphInboxMessage> messages)
    {
        foreach (var message in messages) ApplyCompletedMailAction(message, action);
        StatusText.Text = action switch
        {
            "delete" => messages.Count == 1 ? "Message deleted." : $"{messages.Count} messages deleted.",
            "archive" => messages.Count == 1 ? "Message archived." : $"{messages.Count} messages archived.",
            _ => "Mail action completed."
        };
        _ = SendMicrosoftActionAsync(account, folder, action, messages.Select(m => m.Id).ToArray());
    }

    private async Task SendMicrosoftActionAsync(ConnectedAccount account, MicrosoftFolderSelection folder, string action, string[] ids)
    {
        await _msActionGate.WaitAsync();
        var writer = new GraphMailWriter(_graphHttp, account.AccountId);
        var started = DateTime.UtcNow;
        string[] failed = [];
        Exception? error = null;
        try
        {
            var token = await GetMicrosoftSession(account).GetAccessTokenAsync();
            Action<int>? progress = ids.Length > GraphMailWriter.BatchSize
                ? n => Dispatcher.UIThread.Post(() => StatusText.Text = $"Sending to Microsoft… {n} of {ids.Length}") : null;
            var result = action switch
            {
                "delete" => await writer.MoveManyAsync(token, ids, "deleteditems", progress),
                "archive" => await writer.MoveManyAsync(token, ids, "archive", progress),
                "read" or "unread" => await writer.SetReadManyAsync(token, ids, action == "read", progress),
                _ => await writer.SetFlagManyAsync(token, ids, action == "flag", progress),
            };
            failed = [.. result.Failed.Keys];
            error = result.Failed.Values.FirstOrDefault();
            AppLog.Note("mail-action", $"{action} of {ids.Length} message(s) sent to Microsoft in {(DateTime.UtcNow - started).TotalSeconds:0.0} s" +
                (failed.Length > 0 ? $"; {failed.Length} not accepted ({error!.Message})" : ""));
        }
        catch (Exception e) when (e is GraphMailException or HttpRequestException or ArgumentException or OperationCanceledException)
        {
            failed = ids;                                                                   // nothing was confirmed; the refresh below shows what Microsoft has
            error = e;
        }
        try
        {
            if (failed.Length > 0)
            {
                UndoLocalChange(account, failed);
                foreach (var id in failed) _readOverrides.Remove(GraphMessageKey(account.AccountId, id));
                var lead = failed.Length < ids.Length ? $"{ids.Length - failed.Length} of {ids.Length} done; " : "";
                StatusText.Text = lead + (error is GraphMailException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized }
                    ? "Microsoft declined mail access. Use Account setup to sign in again, then retry."
                    : error is GraphMailException or ArgumentException ? "Microsoft did not accept the change: " + error.Message : "Mail action failed. Check the connection and retry.");
                if (_activeMicrosoftFolder == folder) await RefreshMicrosoftFolderAsync();       // the list shows what Microsoft really has
            }
            else if (ids.Length > GraphMailWriter.BatchSize) StatusText.Text = action switch
            {
                "delete" => $"{ids.Length} messages deleted.",
                "archive" => $"{ids.Length} messages archived.",
                _ => "Mail action completed.",
            };
            if (action is not ("flag" or "unflag") && failed.Length < ids.Length)
            {
                _ = RefreshMicrosoftFolderCountsAsync(account, CancellationToken.None, force: true);
                // Bring the mailbox copy up to date now rather than at the next new-mail check, so the copy matches the server straight away.
                if (Environment.GetEnvironmentVariable("OPENOUTLOOK_NO_MIRROR") != "1" && _mirrorSettings.Load().For(account.AccountId).Enabled)
                    _ = SyncMirrorWhenIdleAsync(account, manual: false);
            }
        }
        finally { _msActionGate.Release(); }
    }

    /// <summary>Right-click Deleted Items > Empty Deleted Items: permanently deletes everything in the folder after a warning.</summary>
    private async Task EmptyMicrosoftDeletedItemsAsync(MicrosoftFolderSelection folder)
    {
        var account = folder.Account;
        if (!account.CanWriteMicrosoftMail)
        {
            await ExplainMailActionAsync("This saved Microsoft sign-in has read-only mail access. Sign in again to allow organizing mail.", account);
            return;
        }
        if (_mailActionBusy) return;
        _mailActionBusy = true;
        await _msActionGate.WaitAsync();
        try
        {
            var writer = new GraphMailWriter(_graphHttp, account.AccountId);
            StatusText.Text = "Checking Deleted Items\u2026";
            var count = await writer.CountDeletedItemsAsync(await GetMicrosoftSession(account).GetAccessTokenAsync(), folder.Id);
            if (count == 0) { StatusText.Text = "Deleted Items is already empty."; return; }
            if (!await ConfirmEmptyDeletedItemsAsync(count, account.DisplayAddress)) { StatusText.Text = "Empty Deleted Items canceled."; return; }
            var deleted = 0;
            string? summary = null;
            try
            {
                await writer.EmptyDeletedItemsAsync(await GetMicrosoftSession(account).GetAccessTokenAsync(), folder.Id,
                    n => Dispatcher.UIThread.Post(() => StatusText.Text = $"Permanently deleting\u2026 {deleted = n} of {count}"));
                summary = $"Deleted Items emptied: {deleted} message{(deleted == 1 ? "" : "s")} permanently deleted.";
                StatusText.Text = summary;
                if (_activeMicrosoftFolder == folder)                          // the list shows it at once; the local copy catches up below
                {
                    _currentGraphMessages = [];
                    ReconcileGraphMessages([]);
                    RefreshItemCount();
                }
            }
            catch (Exception error) when (error is GraphMailException or HttpRequestException or ArgumentException)
            {
                StatusText.Text = $"Deleted Items was only partly emptied ({deleted} deleted): " +
                    (error is GraphMailException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized }
                        ? "Microsoft declined mail access. Use Account setup to sign in again, then retry." : error.Message);
            }
            _ = RefreshMicrosoftFolderCountsAsync(account, CancellationToken.None, force: true);
            // Folders open from the local mailbox copy, which still holds the deleted messages until it syncs: bring it up to date first, or the refresh would put them back.
            await SyncMirrorWhenIdleAsync(account, manual: true);
            if (_activeMicrosoftFolder == folder) await RefreshMicrosoftFolderAsync();
            if (summary is not null) StatusText.Text = summary;
        }
        catch (Exception error) when (error is GraphMailException or HttpRequestException or ArgumentException)
        { StatusText.Text = error is GraphMailException ? error.Message : "Could not reach Microsoft. Check the connection and retry."; }
        finally { _msActionGate.Release(); _mailActionBusy = false; }
    }

    /// <summary>The permanent-delete warning shared by every Deleted Items folder (Microsoft accounts and PST archives).</summary>
    private async Task<bool> ConfirmEmptyDeletedItemsAsync(int count, string where)
    {
        var dialog = new Window
        {
            Title = "Permanently delete all items?", Width = 480, Height = 210,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false
        };
        var delete = new Button { Content = "Delete permanently" };
        var cancel = new Button { Content = "Cancel", IsDefault = true, IsCancel = true };
        delete.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 12, Children =
            {
                new TextBlock { Text = $"Permanently delete {(count == 1 ? "the 1 item" : $"all {count:N0} items")} in Deleted Items ({where})?",
                    FontWeight = Avalonia.Media.FontWeight.SemiBold, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new TextBlock { Text = "This is a permanent delete. The items cannot be recovered afterwards.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { delete, cancel } }
            }
        };
        return await dialog.ShowDialog<bool>(this);
    }
}
