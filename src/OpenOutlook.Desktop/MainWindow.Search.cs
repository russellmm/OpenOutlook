using Avalonia.Controls;
using Avalonia.Interactivity;
using OpenOutlook.Mirror;
using PstCore;

namespace OpenOutlook.Desktop;

/// <summary>
/// The search box. It searches subject, sender, recipients and message text with the PST engine's search (words, "exact phrases", -word,
/// from:/to:/subject:/body:, has:attachment, is:unread), in every folder of the chosen scope including Deleted Items. Hotmail and Gmail are
/// searched in their local mailbox copies (a PST each), so a search never waits for the network.
/// </summary>
public partial class MainWindow
{
    private enum SearchScope { All = 0, Mailbox = 1, Folder = 2 }

    private sealed record SearchTarget(string Path, IPstEngine Store, MailFolder? Folder);
    private sealed record SearchHit(string Path, string StoreName, string FolderName);

    private const int MaxSearchResults = 500;

    private IReadOnlyList<MailSummary>? _searchSummaries;                    // the rows on screen when they are search results (same instances ShowMessages receives)
    private Dictionary<MailSummary, SearchHit>? _searchHits;
    private string _searchQuery = "";
    private string _searchLabel = "";
    private IReadOnlyList<SearchTarget> _searchTargets = [];

    private DataGridColumn SearchFolderColumn => MessageList.Columns[ColumnNames.Length + 1];

    private SearchScope CurrentSearchScope => SearchScopeBox.SelectedIndex is >= 0 and <= 2 ? (SearchScope)SearchScopeBox.SelectedIndex : SearchScope.All;

    private void SearchScopeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SearchScopeBox is null || ArchiveSearchButton is null) return;
        ToolTip.SetTip(ArchiveSearchButton, CurrentSearchScope switch
        {
            SearchScope.Folder => "Search the selected folder and its subfolders (Enter also works)",
            SearchScope.Mailbox => "Search every folder of the selected data file or account, Deleted Items included (Enter also works)",
            _ => "Search every data file and mailbox, Deleted Items included (Enter also works)"
        });
    }

    private async void SearchClicked(object? sender, RoutedEventArgs e)
    {
        var query = ArchiveSearchBox.Text?.Trim() ?? "";
        if (query.Length == 0 || query.Length > 200)
        {
            StatusText.Text = "Enter 1–200 characters to search (words, \"exact phrase\", -word, from:, to:, subject:, body:, has:attachment, is:unread).";
            return;
        }
        var scope = CurrentSearchScope;
        var (targets, label, problem) = ResolveSearchTargets(scope);
        if (problem is not null) { StatusText.Text = problem; return; }
        if (targets.Count == 0) { StatusText.Text = "There is nothing to search yet: open a data file or wait for a mailbox copy to finish its first update."; return; }

        _searchQuery = query;
        _searchTargets = targets;
        _searchLabel = label;
        // Leave the folder that was open: a search result list belongs to no folder, and clicking that folder again must show it again.
        Interlocked.Increment(ref _folderVersion);
        _searchCancellation?.Cancel();
        _onlineCancellation?.Cancel();
        FolderTree.SelectedItem = null;
        await ExecuteSearchAsync(quiet: false);
    }

    /// <summary>The stores and folders the chosen scope covers, from what is selected in the folder tree now.</summary>
    private (IReadOnlyList<SearchTarget> Targets, string Label, string? Problem) ResolveSearchTargets(SearchScope scope)
    {
        if (scope != SearchScope.All)
        {
            if (_activePath is { } path && _stores.TryGetValue(path, out var store))
            {
                if (scope == SearchScope.Mailbox || _activeFolder is null) return ([new SearchTarget(path, store, null)], store.DisplayName, null);
                return ([new SearchTarget(path, store, _activeFolder)], $"{store.DisplayName} › {_activeFolder.Name}", null);
            }
            if (_activeMicrosoftFolder is { } ms) return LocalSearchTarget(ms.Account, ms.Id, ms.Name, scope);
            if (_activeGmailFolder is { } gm) return LocalSearchTarget(gm.Account, gm.LabelId, gm.Name, scope);
            // nothing selected that can be searched on its own: search everything rather than nothing
        }
        var all = _stores.Select(s => new SearchTarget(s.Key, s.Value, null)).ToList();
        return (all, all.Count == 1 ? all[0].Store.DisplayName : $"all {all.Count} data files and mailboxes", null);
    }

    private (IReadOnlyList<SearchTarget> Targets, string Label, string? Problem) LocalSearchTarget(OpenOutlook.Auth.ConnectedAccount account, string folderId, string folderName, SearchScope scope)
    {
        if (LocalCopyOf(account) is not var (store, statePath))
            return ([], "", $"{account.DisplayAddress} has no local mailbox copy to search yet. Turn it on in Account Settings > Data Files, or wait for its first update.");
        var path = _stores.First(s => ReferenceEquals(s.Value, store)).Key;
        if (scope == SearchScope.Mailbox) return ([new SearchTarget(path, store, null)], account.DisplayAddress, null);
        MailFolder? folder;
        try
        {
            using var state = new SyncStateStore(statePath);
            MirrorFolderState? known = folderId.Equals("inbox", StringComparison.OrdinalIgnoreCase)
                ? state.Folders().FirstOrDefault(f => store.FindFolder(f.PstNid) is { Name: "Inbox" } inbox && inbox.ParentNid != 0)
                : state.GetFolder(folderId);
            folder = known is null ? null : store.FindFolder(known.PstNid);
        }
        catch (Exception e) when (e is IOException or Microsoft.Data.Sqlite.SqliteException) { folder = null; }
        return folder is null
            ? ([new SearchTarget(path, store, null)], account.DisplayAddress, null)                // the folder is not in the copy yet: search the whole mailbox
            : ([new SearchTarget(path, store, folder)], $"{account.DisplayAddress} › {folderName}", null);
    }

    /// <summary>Runs the current search over its targets and shows the matches. <paramref name="quiet"/> keeps the list on screen while it runs again (after a delete or move).</summary>
    private async Task ExecuteSearchAsync(bool quiet)
    {
        _searchCancellation?.Cancel();
        _searchCancellation = new CancellationTokenSource();
        var ct = _searchCancellation.Token;
        var version = Interlocked.Increment(ref _folderVersion);
        Interlocked.Increment(ref _messageVersion);
        var query = _searchQuery;
        var targets = _searchTargets;
        if (!quiet)
        {
            _currentMessages = null; _currentGraphMessages = null; _graphRows = null; _searchSummaries = null; _searchHits = null;
            _activeMicrosoftAccount = null; _activeMicrosoftFolder = null; _activeGmailFolder = null;
            _activePath = null; _activeFolder = null;
            HideSpamBar();
            MessageList.ItemsSource = null; RefreshItemCount();
            ClearReader();
        }
        StatusText.Text = $"Searching {_searchLabel} for “{query}”…";
        var found = new List<(SearchTarget Target, MailSummary Summary, string Folder)>();
        var failed = new List<string>();
        try
        {
            var done = 0;
            foreach (var target in targets)
            {
                ct.ThrowIfCancellationRequested();
                if (targets.Count > 1) StatusText.Text = $"Searching {target.Store.DisplayName} for “{query}” ({++done} of {targets.Count})…";
                await _readerGate.WaitAsync(ct);                       // one store at a time, so folder browsing is held up only briefly
                try
                {
                    if (!_stores.TryGetValue(target.Path, out var current) || !ReferenceEquals(current, target.Store)) continue;
                    var matches = await Task.Run(() => target.Store.Search(query, target.Folder), ct);
                    foreach (var m in matches) found.Add((target, m, target.Store.FindFolder(m.FolderNid)?.Name ?? ""));
                }
                catch (Exception e) when (e is PstException or IOException or ObjectDisposedException or InvalidOperationException)
                {
                    AppLog.Error("search", e, $"search failed in {target.Store.DisplayName}");
                    failed.Add(target.Store.DisplayName);
                }
                finally { _readerGate.Release(); }
            }
            if (ct.IsCancellationRequested || version != _folderVersion) return;

            var ordered = found.OrderByDescending(f => f.Summary.Received == DateTime.MinValue ? f.Summary.Sent : f.Summary.Received).ToList();
            var truncated = ordered.Count > MaxSearchResults;
            if (truncated) ordered = ordered.Take(MaxSearchResults).ToList();
            var summaries = ordered.Select(f => f.Summary).ToList();
            _searchHits = ordered.ToDictionary(f => f.Summary, f => new SearchHit(f.Target.Path, f.Target.Store.DisplayName, f.Folder));
            _searchSummaries = summaries;
            ShowMessages(summaries);
            var note = (truncated ? $" (newest {MaxSearchResults} shown)" : "") +
                       (failed.Count > 0 ? $" · could not search {string.Join(", ", failed)}" : "") +
                       (targets.Any(t => IsMirrorCopy(t.Path)) ? " · mailboxes are searched in their local copies" : "");
            StatusText.Text = summaries.Count == 0
                ? $"No matches for “{query}” in {_searchLabel}{note}"
                : $"{summaries.Count} match{(summaries.Count == 1 ? "" : "es")} for “{query}” in {_searchLabel}{note}";
        }
        catch (OperationCanceledException) { /* a newer search or a folder selection superseded this one */ }
        catch (Exception ex) { if (version == _folderVersion) StatusText.Text = $"Search failed: {ex.Message}"; }
    }

    /// <summary>Leaves search results behind (a folder was selected).</summary>
    private void EndSearchResults()
    {
        _searchSummaries = null;
        _searchHits = null;
        SearchFolderColumn.IsVisible = false;
    }

    /// <summary>When a search result is selected the window acts on that message's own data file and folder, as if its folder were open.</summary>
    private void EnterSearchRow(MessageListRow row)
    {
        if (row.StorePath is not { } path || !_stores.TryGetValue(path, out var store)) return;
        _activeMicrosoftAccount = null; _activeMicrosoftFolder = null; _activeGmailFolder = null;
        _activePath = path;
        _activeFolder = store.FindFolder(row.Summary.FolderNid);
    }
}
