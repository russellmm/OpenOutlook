using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using OpenOutlook.Auth;
using OpenOutlook.JunkCleaner;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Desktop;

/// <summary>
/// Inbox spam check (owner 2026-10-29): the newest 200 messages of a Microsoft Inbox are matched against the account's Inbox spam rules (its own
/// set, see <see cref="InboxSpamCheckAccount"/>), the Junk Cleaner keywords and Blocked Senders. Suspects get a ticked check box in the list's first
/// column; the bar above the list removes the ticked ones with Block Sender and Delete (senders to Blocked Senders, messages to Deleted Items) or
/// Delete. Unticking remembers the message as not spam. Nothing moves without a click; mail the Safe lists or contacts trust is never marked.
/// </summary>
public partial class MainWindow
{
    private readonly InboxSpamCheckStore _spamStore = new(Path.GetDirectoryName(new OpenOutlook.Mirror.MirrorSettingsStore().Path)!);
    private readonly Dictionary<string, SpamScan> _spamScans = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _spamStarted = new(StringComparer.Ordinal);
    private readonly HashSet<string> _spamBusy = new(StringComparer.Ordinal);
    private static readonly TimeSpan SpamRecheck = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SpamMinGap = TimeSpan.FromSeconds(30);

    /// <summary>One finished check of an account's Inbox: when, which messages were looked at, and the suspects by message id.</summary>
    private sealed record SpamScan(DateTime At, HashSet<string> Scanned, Dictionary<string, JunkCleanerHit> Suspects);

    /// <summary>Test seam: replaces the Graph scan of the Inbox.</summary>
    internal Func<ConnectedAccount, InboxSpamCheckAccount, Task<JunkCleanerRunResult>>? SpamScanOverride { get; set; }
    /// <summary>Test seam: answers the Block Sender and Delete confirmation.</summary>
    internal Func<IReadOnlyList<string>, int, Task<bool>>? SpamBlockConfirmOverride { get; set; }

    /// <summary>The check box column: the last column of the grid, always shown first; not part of view-layout.json.</summary>
    private DataGridColumn SpamColumn => MessageList.Columns[ColumnNames.Length];

    private ConnectedAccount? SpamInboxAccount =>
        _activeMicrosoftFolder is { Id: "inbox" } folder && folder.Account.Provider == OAuthProvider.MicrosoftConsumers && _graphRows is not null ? folder.Account : null;

    /// <summary>After the list was filled or changed: marks the known suspects and starts a check when new mail arrived or the last one is old.</summary>
    private void UpdateSpamMarks()
    {
        if (SpamInboxAccount is not { } account || !_spamStore.For(account.AccountId).Enabled)
        {
            HideSpamBar();
            return;
        }
        var settings = _spamStore.For(account.AccountId);
        _spamScans.TryGetValue(account.AccountId, out var scan);
        if (scan is not null)
        {
            var notSpam = settings.NotSpam.ToHashSet(StringComparer.Ordinal);
            foreach (var row in _graphRows!)
                row.SetSpam(scan.Suspects.TryGetValue(row.Message.Id, out var hit) ? hit.Reasons : null, !notSpam.Contains(row.Message.Id));
        }
        UpdateSpamBar();
        var due = scan is null || DateTime.UtcNow - scan.At > SpamRecheck ||
            _graphRows!.OrderByDescending(r => r.ReceivedSort).Take(50).Any(r => !scan.Scanned.Contains(r.Message.Id));
        if (due) _ = RunSpamCheckAsync(account, force: false);
    }

    private void HideSpamBar()
    {
        SpamBar.IsVisible = false;
        SpamColumn.IsVisible = false;
    }

    private void UpdateSpamBar()
    {
        if (SpamInboxAccount is not { } account || _graphRows is null) { HideSpamBar(); return; }
        var suspects = _graphRows.Where(r => r.IsSpamSuspect).ToList();
        var ticked = suspects.Count(r => r.IsSpamTicked);
        SpamBar.IsVisible = true;                     // always while the check is on, so it is seen to run (owner 2026-10-29: "no indication that there is a spam check")
        if (suspects.Count > 0 && !SpamColumn.IsVisible)
        {
            SpamColumn.IsVisible = true;
            SpamColumn.DisplayIndex = 0;
        }
        else if (suspects.Count == 0) SpamColumn.IsVisible = false;
        var scan = _spamScans.GetValueOrDefault(account.AccountId);
        SpamBarText.Text = scan is null && suspects.Count == 0 ? "Checking the Inbox for spam..."
            : suspects.Count == 0 ? $"Spam check: no suspected spam in the newest {scan!.Scanned.Count} Inbox messages."
            : suspects.Count == 1
            ? (ticked == 1 ? "1 message looks like spam and is ticked. Untick it if it is not spam." : "1 message looked like spam; you unticked it.")
            : $"{suspects.Count} messages look like spam; {ticked} ticked. Untick any that are not spam.";
        SpamBlockButton.IsEnabled = SpamDeleteButton.IsEnabled = ticked > 0;
    }

    /// <summary>Scans the account's Inbox (at most one scan per account at a time, and not again within 30 seconds unless forced).</summary>
    internal async Task RunSpamCheckAsync(ConnectedAccount account, bool force)
    {
        var id = account.AccountId;
        if (_spamBusy.Contains(id)) return;
        if (!force && _spamStarted.TryGetValue(id, out var started) && DateTime.UtcNow - started < SpamMinGap) return;
        var settings = _spamStore.For(id);
        if (!settings.Enabled) return;
        _spamBusy.Add(id);
        _spamStarted[id] = DateTime.UtcNow;
        try
        {
            var result = SpamScanOverride is { } scan ? await scan(account, settings) : await ScanInboxForSpamAsync(account, settings);
            _spamScans[id] = new SpamScan(DateTime.UtcNow, (result.ScannedIds ?? []).ToHashSet(StringComparer.Ordinal),
                result.Matched.ToDictionary(h => h.MessageId, StringComparer.Ordinal));
            AppLog.Note("spam-check", $"{account.DisplayAddress}: {result.Scanned} Inbox messages checked, {result.Matched.Count} look like spam");
            if (force) StatusText.Text = result.Matched.Count == 0 ? "No spam found in the Inbox." : $"{result.Matched.Count} Inbox message{(result.Matched.Count == 1 ? " looks" : "s look")} like spam.";
        }
        catch (Exception e) when (e is GraphMailException or HttpRequestException or ArgumentException or OperationCanceledException or InvalidOperationException)
        {
            AppLog.Error("spam-check", e, $"checking the Inbox of {account.DisplayAddress} failed");
            if (force) StatusText.Text = "The spam check could not read the Inbox: " + e.Message;
            return;
        }
        finally { _spamBusy.Remove(id); }
        if (SpamInboxAccount?.AccountId == id) UpdateSpamMarks();
    }

    private async Task<JunkCleanerRunResult> ScanInboxForSpamAsync(ConnectedAccount account, InboxSpamCheckAccount settings)
    {
        var junk = JunkSettingsFor(account.AccountId);
        var rules = new JunkCleanerAccountSettings
        {
            AccountId = account.AccountId, Enabled = true, Rules = settings.Rules, Brands = junk.Brands,
            Keywords = settings.UseKeywords ? junk.Keywords : [],
        };
        var lists = JunkListsStore.Load(account.AccountId);
        HashSet<string>? contacts = null;
        try { contacts = await TrustedContactsAsync(account, lists); }
        catch (Exception e) when (e is GraphMailException or HttpRequestException or InvalidOperationException)
        { AppLog.Error("spam-check", e, "reading the contacts failed; checking without them"); }
        return await MakeJunkRunner(account).ScanInboxAsync(rules, 200, default, lists.Effective, contacts);
    }

    /// <summary>Ticking or unticking a message's box: an unticked suspect is remembered as not spam.</summary>
    private void SpamTickClicked(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not GraphMessageListRow row || SpamInboxAccount is not { } account) return;
        SetSpamTick(account, row, row.IsSpamTicked);
    }

    internal void SetSpamTick(ConnectedAccount account, GraphMessageListRow row, bool ticked)
    {
        row.IsSpamTicked = ticked;
        try { _spamStore.Save(InboxSpamCheckStore.WithNotSpam(_spamStore.For(account.AccountId), row.Message.Id, !ticked)); }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException)
        { StatusText.Text = "Could not remember the choice: " + x.Message; }
        UpdateSpamBar();
    }

    private IReadOnlyList<GraphMessageListRow> TickedSpamRows() =>
        _graphRows?.Where(r => r.IsSpamSuspect && r.IsSpamTicked).ToList() ?? [];

    private async void SpamBlockClicked(object? sender, RoutedEventArgs e) => await BlockAndDeleteTickedSpamAsync();
    private void SpamDeleteClicked(object? sender, RoutedEventArgs e) => DeleteTickedSpam();

    /// <summary>Adds the ticked messages' senders to Blocked Senders (after a confirmation that names them), then deletes the messages.</summary>
    internal async Task BlockAndDeleteTickedSpamAsync()
    {
        if (SpamInboxAccount is not { } account || _activeMicrosoftFolder is not { } folder) return;
        var rows = TickedSpamRows();
        if (rows.Count == 0) return;
        if (!account.CanWriteMicrosoftMail)
        {
            await ExplainMailActionAsync("This saved Microsoft sign-in has read-only mail access. Sign in again to allow organizing mail.", account);
            return;
        }
        var scan = _spamScans.GetValueOrDefault(account.AccountId);
        var senders = rows.Select(r => JunkLists.Address(r.Message.FromAddress) ??
                (scan?.Suspects.TryGetValue(r.Message.Id, out var hit) == true ? JunkLists.Address(hit.Sender) : null))
            .OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (!await ConfirmSpamBlockAsync(senders, rows.Count)) return;
        if (senders.Count > 0)
        {
            var lists = JunkListsStore.Load(account.AccountId).WithAdded(JunkListKind.BlockedSenders, senders.ToArray());
            SaveJunkLists(lists);
        }
        AppLog.Note("spam-check", $"Block Sender and Delete ({account.DisplayAddress}): {string.Join(", ", senders)}; {rows.Count} message(s) to Deleted Items");
        DeleteSpamRows(account, folder, rows);
        StatusText.Text = $"{(senders.Count == 1 ? senders[0] : $"{senders.Count} senders")} added to Blocked Senders; " +
            (rows.Count == 1 ? "1 message deleted." : $"{rows.Count} messages deleted.");
    }

    internal void DeleteTickedSpam()
    {
        if (SpamInboxAccount is not { } account || _activeMicrosoftFolder is not { } folder) return;
        var rows = TickedSpamRows();
        if (rows.Count == 0) return;
        if (!account.CanWriteMicrosoftMail)
        {
            _ = ExplainMailActionAsync("This saved Microsoft sign-in has read-only mail access. Sign in again to allow organizing mail.", account);
            return;
        }
        AppLog.Note("spam-check", $"Delete ({account.DisplayAddress}): {rows.Count} message(s) to Deleted Items");
        DeleteSpamRows(account, folder, rows);
    }

    /// <summary>Moves the rows' messages to Deleted Items like the Delete button (the list changes at once; a refusal puts them back).</summary>
    private void DeleteSpamRows(ConnectedAccount account, MicrosoftFolderSelection folder, IReadOnlyList<GraphMessageListRow> rows)
    {
        var messages = rows.Select(r => r.Message).ToList();
        if (_spamScans.TryGetValue(account.AccountId, out var scan))
            foreach (var m in messages) scan.Suspects.Remove(m.Id);
        ActAtOnce(account, folder, "delete", messages);
        UpdateSpamBar();
    }

    private async Task<bool> ConfirmSpamBlockAsync(IReadOnlyList<string> senders, int count)
    {
        if (SpamBlockConfirmOverride is { } answer) return await answer(senders, count);
        var dialog = new Window
        {
            Title = "Block Sender and Delete", Width = 480, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Themed.Brush("OlCard", Brushes.White),
        };
        var ok = new Button { Content = "Block and Delete", MinWidth = 120, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true };
        ok.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        var who = senders.Count == 0 ? "The ticked messages have no sender address to block."
            : (senders.Count == 1 ? "This sender is" : $"These {senders.Count} senders are") + " added to Blocked Senders:";
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16), Spacing = 10, Children =
            {
                new TextBlock { Text = who, TextWrapping = TextWrapping.Wrap },
                new TextBox { Text = string.Join(Environment.NewLine, senders), IsReadOnly = true, AcceptsReturn = true, MaxHeight = 180, FontSize = 12, IsVisible = senders.Count > 0 },
                new TextBlock { Text = (count == 1 ? "The message is" : $"The {count} ticked messages are") + " moved to Deleted Items.", TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
            }
        };
        return await dialog.ShowDialog<bool>(this);
    }

    /// <summary>The bar's ··· menu.</summary>
    private void SpamMoreClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control button || SpamInboxAccount is not { } account) return;
        MenuItem Item(string text, Action run)
        {
            var item = new MenuItem { Header = text };
            item.Click += (_, _) => run();
            return item;
        }
        var flyout = new MenuFlyout
        {
            Items =
            {
                Item("Tick all", () => SetAllSpamTicks(account, true)),
                Item("Untick all", () => SetAllSpamTicks(account, false)),
                new Separator(),
                Item("Check again", () => _ = RunSpamCheckAsync(account, force: true)),
                Item("Inbox spam rules...", () => _ = EditSpamRulesAsync(account)),
                Item("Turn off the Inbox spam check", () => SetSpamCheckEnabled(account, false)),
            }
        };
        flyout.ShowAt(button);
    }

    private void SetAllSpamTicks(ConnectedAccount account, bool ticked)
    {
        if (_graphRows is null) return;
        var settings = _spamStore.For(account.AccountId);
        foreach (var row in _graphRows.Where(r => r.IsSpamSuspect))
        {
            row.IsSpamTicked = ticked;
            settings = InboxSpamCheckStore.WithNotSpam(settings, row.Message.Id, !ticked);
        }
        try { _spamStore.Save(settings); }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { StatusText.Text = "Could not remember the choice: " + x.Message; }
        UpdateSpamBar();
    }

    internal void SetSpamCheckEnabled(ConnectedAccount account, bool enabled)
    {
        try { _spamStore.Save(_spamStore.For(account.AccountId) with { Enabled = enabled }); }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { StatusText.Text = "Could not save the setting: " + x.Message; return; }
        if (!enabled)
        {
            _spamScans.Remove(account.AccountId);
            foreach (var row in _graphRows ?? []) row.SetSpam(null, false);
        }
        StatusText.Text = enabled ? $"The Inbox spam check is on for {account.DisplayAddress}." : $"The Inbox spam check is off for {account.DisplayAddress}. Turn it on again from Home > Junk.";
        AppLog.Note("spam-check", $"Inbox spam check {(enabled ? "on" : "off")} for {account.DisplayAddress}");
        if (enabled && SpamInboxAccount?.AccountId == account.AccountId) _ = RunSpamCheckAsync(account, force: true);
        UpdateSpamMarks();
    }

    /// <summary>The Inbox spam rules in the Junk Cleaner's rules window; the company list is the one the Junk Cleaner uses.</summary>
    internal async Task EditSpamRulesAsync(ConnectedAccount account)
    {
        var settings = _spamStore.For(account.AccountId);
        var junk = JunkSettingsFor(account.AccountId);
        var window = new JunkRulesWindow(account.DisplayAddress, settings.Rules, junk.Brands, "Inbox spam rules");
        await window.ShowDialog(this);
        if (window.Result is not { } result) return;
        try { _spamStore.Save(settings with { Rules = result.Rules }); }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { StatusText.Text = "Could not save the rules: " + x.Message; return; }
        if (!(result.Brands ?? []).SequenceEqual(junk.Brands ?? [])) SaveJunkSettings(junk with { Brands = result.Brands });
        AppLog.Note("spam-check", $"Inbox spam rules for {account.DisplayAddress}: {JunkRulesWindow.Summary(result.Rules)}");
        _spamScans.Remove(account.AccountId);
        _ = RunSpamCheckAsync(account, force: true);
    }

    /// <summary>
    /// Home > Junk / right-click Junk: the check's on/off switch and its rules, reachable while no message is marked. The right-click menu is
    /// built once, so the account (the open folder's, else the first Microsoft account) is looked up when the items show and when clicked.
    /// </summary>
    private IEnumerable<Control> SpamCheckMenuItems()
    {
        ConnectedAccount? Account() => _activeMicrosoftFolder?.Account ?? JunkListAccounts().FirstOrDefault();
        var tick = new CheckBox { IsHitTestVisible = false, MinWidth = 0, Padding = new Avalonia.Thickness(0) };
        var on = new MenuItem { Header = "Check the Inbox for _Spam", Tag = "spamCheck", Icon = tick };
        var rules = new MenuItem { Header = "Inbox Spam _Rules...", Tag = "spamRules" };
        on.AttachedToVisualTree += (_, _) =>
        {
            var account = Account();
            tick.IsChecked = account is not null && _spamStore.For(account.AccountId).Enabled;
            on.IsEnabled = rules.IsEnabled = account is not null;
        };
        on.Click += (_, _) => { if (Account() is { } a) SetSpamCheckEnabled(a, !_spamStore.For(a.AccountId).Enabled); };
        rules.Click += (_, _) => { if (Account() is { } a) _ = EditSpamRulesAsync(a); };
        return [on, rules];
    }
}
