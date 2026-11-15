using Avalonia.Interactivity;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Desktop;

/// <summary>
/// "There are more items in this folder on the server" (as Outlook shows at the end of a folder in cached mode): a Microsoft folder is shown from its local mailbox copy,
/// which keeps only the newest part of a large folder (Deleted Items especially). The bar below the list appears when the server holds more than the list does,
/// and one click fetches the next 100 older messages from Microsoft and adds them to the list.
/// </summary>
public partial class MainWindow
{
    private const int MoreOnServerPage = 100;

    private readonly Dictionary<string, GraphInboxMessage> _serverExtras = new(StringComparer.Ordinal);       // older messages fetched with "view more", for the open folder
    private string? _serverExtrasFolder;                                                                          // accountId|folderId the extras belong to
    private bool _moreOnServerBusy;

    private static string FolderKey(MicrosoftFolderSelection folder) => folder.Account.AccountId + "|" + folder.Id;

    /// <summary>Leaving a folder (or showing something that is not a folder list) drops the bar and the extra messages.</summary>
    private void ResetMoreOnServer()
    {
        MoreOnServerBar.IsVisible = false;
        _serverExtras.Clear();
        _serverExtrasFolder = null;
    }

    /// <summary>The list for a refresh of the open folder: what the copy or Microsoft sent, plus the older messages already fetched with "view more".</summary>
    private IReadOnlyList<GraphInboxMessage> WithServerExtras(MicrosoftFolderSelection folder, IReadOnlyList<GraphInboxMessage> messages)
    {
        if (_serverExtras.Count == 0 || _serverExtrasFolder != FolderKey(folder)) return messages;
        var have = messages.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        return [.. messages, .. _serverExtras.Values.Where(m => !have.Contains(m.Id))];
    }

    /// <summary>Asks Microsoft how many items the folder holds and shows the bar when the list has fewer. Cosmetic: a failure leaves the bar as it was.</summary>
    private async Task UpdateMoreOnServerAsync(MicrosoftFolderSelection folder, long version, CancellationToken cancellationToken)
    {
        try
        {
            var token = await GetMicrosoftSession(folder.Account).GetAccessTokenAsync(cancellationToken);
            var (total, _) = await new GraphInboxReader(_graphHttp, folder.Account.AccountId).GetFolderCountsAsync(token, folder.Id, cancellationToken);
            if (version != _folderVersion || cancellationToken.IsCancellationRequested || _activeMicrosoftFolder != folder || _graphRows is null) return;
            MoreOnServerBar.IsVisible = total > _graphRows.Count;
        }
        catch (Exception e) when (e is GraphMailException or HttpRequestException or OperationCanceledException or ArgumentException) { }
    }

    private async void MoreOnServerClicked(object? sender, RoutedEventArgs e)
    {
        if (_moreOnServerBusy || _activeMicrosoftFolder is not { } folder || _graphRows is null) return;
        _moreOnServerBusy = true;
        MoreOnServerButton.IsEnabled = false;
        var version = _folderVersion;
        try
        {
            StatusText.Text = $"Getting more {folder.Name} items from Microsoft…";
            var token = await GetMicrosoftSession(folder.Account).GetAccessTokenAsync();
            var reader = new GraphInboxReader(_graphHttp, folder.Account.AccountId);
            var skip = _graphRows.Count;
            var added = new List<GraphInboxMessage>();
            for (var attempt = 0; attempt < 5 && added.Count == 0; attempt++)           // a page of messages already in the list is skipped over
            {
                var page = await reader.GetMoreMessagesAsync(token, folder.Id, skip, MoreOnServerPage);
                if (version != _folderVersion || _activeMicrosoftFolder != folder || _graphRows is null) return;
                if (page.Count == 0) break;
                var have = _graphRows.Select(r => r.Message.Id).ToHashSet(StringComparer.Ordinal);
                added.AddRange(page.Where(m => have.Add(m.Id)));
                skip += page.Count;
            }
            _serverExtrasFolder = FolderKey(folder);
            foreach (var message in added)
            {
                _serverExtras[message.Id] = message;
                _graphRows.Add(new GraphMessageListRow(WithLocalReadState(message)));
            }
            _currentGraphMessages = _graphRows.Select(r => r.Message).ToArray();
            RefreshItemCount();
            UpdateSpamMarks();
            StatusText.Text = added.Count == 0 ? $"No more {folder.Name} items on the server."
                : $"{added.Count} more item{(added.Count == 1 ? "" : "s")} from Microsoft · {_graphRows.Count:N0} shown · {folder.Account.DisplayAddress}";
            if (added.Count == 0) MoreOnServerBar.IsVisible = false;
            else await UpdateMoreOnServerAsync(folder, version, CancellationToken.None);
        }
        catch (Exception error) when (error is GraphMailException or HttpRequestException or ArgumentException)
        {
            StatusText.Text = error is GraphMailException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized }
                ? "Microsoft declined mail access. Use Account setup to sign in again, then retry."
                : "Could not get more items from Microsoft. Check the connection and retry.";
        }
        finally { _moreOnServerBusy = false; MoreOnServerButton.IsEnabled = true; }
    }
}
