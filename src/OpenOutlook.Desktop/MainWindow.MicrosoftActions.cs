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

    /// <summary>Applies the action to the list now, then sends it (one request at a time, in order). A refusal puts the list back and says why.</summary>
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
        var done = 0;
        try
        {
            foreach (var id in ids)
            {
                var token = await GetMicrosoftSession(account).GetAccessTokenAsync();
                switch (action)
                {
                    case "delete": await writer.MoveAsync(token, id, "deleteditems"); break;
                    case "archive": await writer.MoveAsync(token, id, "archive"); break;
                    case "read": await writer.SetReadAsync(token, id, true); break;
                    case "unread": await writer.SetReadAsync(token, id, false); break;
                    case "flag": await writer.SetFlagAsync(token, id, true); break;
                    case "unflag": await writer.SetFlagAsync(token, id, false); break;
                }
                done++;
            }
            if (action is not ("flag" or "unflag")) _ = RefreshMicrosoftFolderCountsAsync(account, CancellationToken.None, force: true);
        }
        catch (Exception error) when (error is GraphMailException or HttpRequestException or ArgumentException or OperationCanceledException)
        {
            var failed = ids.Skip(done).ToArray();
            UndoLocalChange(account, failed);
            foreach (var id in failed) _readOverrides.Remove(GraphMessageKey(account.AccountId, id));
            StatusText.Text = error is GraphMailException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized }
                ? "Microsoft declined mail access. Use Account setup to sign in again, then retry."
                : error is GraphMailException or ArgumentException ? "Microsoft did not accept the change: " + error.Message : "Mail action failed. Check the connection and retry.";
            if (_activeMicrosoftFolder == folder) await RefreshMicrosoftFolderAsync();           // the list shows what Microsoft really has
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
            try
            {
                await writer.EmptyDeletedItemsAsync(await GetMicrosoftSession(account).GetAccessTokenAsync(), folder.Id,
                    n => Dispatcher.UIThread.Post(() => StatusText.Text = $"Permanently deleting\u2026 {deleted = n} of {count}"));
                StatusText.Text = $"Deleted Items emptied: {deleted} message{(deleted == 1 ? "" : "s")} permanently deleted.";
            }
            catch (Exception error) when (error is GraphMailException or HttpRequestException or ArgumentException)
            {
                StatusText.Text = $"Deleted Items was only partly emptied ({deleted} deleted): " +
                    (error is GraphMailException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized }
                        ? "Microsoft declined mail access. Use Account setup to sign in again, then retry." : error.Message);
            }
            _ = RefreshMicrosoftFolderCountsAsync(account, CancellationToken.None, force: true);
            if (_activeMicrosoftFolder == folder) await RefreshMicrosoftFolderAsync();
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
