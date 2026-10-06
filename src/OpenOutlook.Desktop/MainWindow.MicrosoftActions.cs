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
}
