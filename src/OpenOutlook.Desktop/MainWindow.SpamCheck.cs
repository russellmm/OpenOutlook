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
/// The same marks show in Junk Email (owner, same day): there they are what the Junk Cleaner's keywords and rules would delete, and a message
/// unticked there is kept by the Junk Cleaner (manual and automatic runs), so the ticks are exactly what a clean removes.
/// </summary>
public partial class MainWindow
{
    private readonly InboxSpamCheckStore _spamStore = new(Path.GetDirectoryName(new OpenOutlook.Mirror.MirrorSettingsStore().Path)!);
    private readonly Dictionary<string, SpamScan> _spamScans = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _spamStarted = new(StringComparer.Ordinal);
    private readonly HashSet<string> _spamBusy = new(StringComparer.Ordinal);
    private static readonly TimeSpan SpamRecheck = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SpamMinGap = TimeSpan.FromSeconds(30);

    /// <summary>One finished check of an account's Inbox or Junk Email: when, which messages were looked at, and the suspects by message id.</summary>
    private sealed record SpamScan(DateTime At, HashSet<string> Scanned, Dictionary<string, JunkCleanerHit> Suspects);

    /// <summary>The folder the marks are for: a Microsoft Inbox, or its Junk Email folder (<paramref name="Junk"/>).</summary>
    private readonly record struct SpamFolder(ConnectedAccount Account, bool Junk)
    {
        public string Key => Junk ? Account.AccountId + "|junk" : Account.AccountId;
        public string Name => Junk ? "Junk Email" : "Inbox";
    }

    /// <summary>Test seam: replaces the Graph scan of the Inbox (junk = false) or the Junk Email folder (junk = true).</summary>
    internal Func<ConnectedAccount, bool, Task<JunkCleanerRunResult>>? SpamScanOverride { get; set; }
    /// <summary>Test seam: answers the Block Sender and Delete confirmation.</summary>
    internal Func<IReadOnlyList<string>, int, Task<bool>>? SpamBlockConfirmOverride { get; set; }

    /// <summary>The check box column: the last column of the grid, always shown first; not part of view-layout.json.</summary>
    private DataGridColumn SpamColumn => MessageList.Columns[ColumnNames.Length];

    private SpamFolder? SpamTarget =>
        _activeMicrosoftFolder is { } folder && folder.Account.Provider == OAuthProvider.MicrosoftConsumers && _graphRows is not null
            ? folder.Id == "inbox" ? new SpamFolder(folder.Account, false) : InJunkEmailFolder ? new SpamFolder(folder.Account, true) : null
            : null;

    /// <summary>After the list was filled or changed: marks the known suspects and starts a check when new mail arrived or the last one is old.</summary>
    private void UpdateSpamMarks()
    {
        if (SpamTarget is not { } target || !target.Junk && !_spamStore.For(target.Account.AccountId).Enabled)
        {
            HideSpamBar();
            return;
        }
        var settings = _spamStore.For(target.Account.AccountId);
        _spamScans.TryGetValue(target.Key, out var scan);
        if (scan is not null)
        {
            var unticked = (target.Junk ? settings.KeepInJunk : settings.NotSpam).ToHashSet(StringComparer.Ordinal);
            foreach (var row in _graphRows!)
                row.SetSpam(scan.Suspects.TryGetValue(row.Message.Id, out var hit) ? hit.Reasons : null, !unticked.Contains(row.Message.Id));
        }
        UpdateSpamBar();
        var due = scan is null || DateTime.UtcNow - scan.At > SpamRecheck ||
            _graphRows!.OrderByDescending(r => r.ReceivedSort).Take(50).Any(r => !scan.Scanned.Contains(r.Message.Id));
        if (due) _ = RunSpamCheckAsync(target.Account, force: false, target.Junk);
    }

    private void HideSpamBar()
    {
        SpamBar.IsVisible = false;
        SpamColumn.IsVisible = false;
    }

    private void UpdateSpamBar()
    {
        if (SpamTarget is not { } target || _graphRows is null) { HideSpamBar(); return; }
        var suspects = _graphRows.Where(r => r.IsSpamSuspect).ToList();
        var ticked = suspects.Count(r => r.IsSpamTicked);
        SpamBar.IsVisible = true;                     // always while the check is on, so it is seen to run (owner 2026-10-29: "no indication that there is a spam check")
        if (suspects.Count > 0 && !SpamColumn.IsVisible)
        {
            SpamColumn.IsVisible = true;
            SpamColumn.DisplayIndex = 0;
        }
        else if (suspects.Count == 0) SpamColumn.IsVisible = false;
        var scan = _spamScans.GetValueOrDefault(target.Key);
        SpamBarText.Text = target.Junk ? JunkBarText(target.Account, scan, suspects.Count, ticked)
            : scan is null && suspects.Count == 0 ? "Checking the Inbox for spam..."
            : suspects.Count == 0 ? $"Spam check: no suspected spam in the newest {scan!.Scanned.Count} Inbox messages."
            : suspects.Count == 1
            ? (ticked == 1 ? "1 message looks like spam and is ticked. Untick it if it is not spam." : "1 message looked like spam; you unticked it.")
            : $"{suspects.Count} messages look like spam; {ticked} ticked. Untick any that are not spam.";
        SpamBlockButton.IsEnabled = SpamDeleteButton.IsEnabled = ticked > 0;
    }

    /// <summary>The bar in Junk Email: what the Junk Cleaner's keywords and rules match, and when the ticked ones go.</summary>
    private string JunkBarText(ConnectedAccount account, SpamScan? scan, int suspects, int ticked)
    {
        if (scan is null && suspects == 0) return "Checking Junk Email against the Junk Cleaner's keywords and rules...";
        if (suspects == 0) return $"Junk Cleaner: none of the {scan!.Scanned.Count} Junk Email messages matches its keywords and rules.";
        var settings = JunkSettingsFor(account.AccountId);
        var when = !settings.Enabled ? "The Junk Cleaner is off, so nothing is deleted until you click Delete."
            : settings.AutoClean ? $"Ticked ones are deleted at the next automatic clean (every {settings.IntervalMinutes} min) or now with Delete."
            : "Ticked ones are deleted by Clean Junk Folder or now with Delete.";
        return (suspects == 1 ? $"1 message matches the Junk Cleaner's keywords and rules; {ticked} ticked. "
            : $"{suspects} messages match the Junk Cleaner's keywords and rules; {ticked} ticked. ") + when + " Untick any you want to keep.";
    }

    /// <summary>Scans the account's Inbox or Junk Email (one scan per folder at a time, and not again within 30 seconds unless forced).</summary>
    internal async Task RunSpamCheckAsync(ConnectedAccount account, bool force, bool junk = false)
    {
        var target = new SpamFolder(account, junk);
        if (_spamBusy.Contains(target.Key)) return;
        if (!force && _spamStarted.TryGetValue(target.Key, out var started) && DateTime.UtcNow - started < SpamMinGap) return;
        if (!junk && !_spamStore.For(account.AccountId).Enabled) return;
        _spamBusy.Add(target.Key);
        _spamStarted[target.Key] = DateTime.UtcNow;
        try
        {
            var result = SpamScanOverride is { } scan ? await scan(account, junk)
                : junk ? await ScanJunkForMarksAsync(account) : await ScanInboxForSpamAsync(account, _spamStore.For(account.AccountId));
            _spamScans[target.Key] = new SpamScan(DateTime.UtcNow, (result.ScannedIds ?? []).ToHashSet(StringComparer.Ordinal),
                result.Matched.ToDictionary(h => h.MessageId, StringComparer.Ordinal));
            AppLog.Note("spam-check", $"{account.DisplayAddress}: {result.Scanned} {target.Name} messages checked, {result.Matched.Count} " + (junk ? "match the Junk Cleaner" : "look like spam"));
            if (force) StatusText.Text = result.Matched.Count == 0 ? $"Nothing marked in {target.Name}." : $"{result.Matched.Count} {target.Name} message{(result.Matched.Count == 1 ? "" : "s")} marked.";
        }
        catch (Exception e) when (e is GraphMailException or HttpRequestException or ArgumentException or OperationCanceledException or InvalidOperationException)
        {
            AppLog.Error("spam-check", e, $"checking {target.Name} of {account.DisplayAddress} failed");
            if (force) StatusText.Text = $"The check could not read {target.Name}: " + e.Message;
            return;
        }
        finally { _spamBusy.Remove(target.Key); }
        if (SpamTarget?.Key == target.Key) UpdateSpamMarks();
    }

    private async Task<HashSet<string>?> SpamContactsAsync(ConnectedAccount account, JunkListsAccount lists)
    {
        try { return await TrustedContactsAsync(account, lists); }
        catch (Exception e) when (e is GraphMailException or HttpRequestException or InvalidOperationException)
        { AppLog.Error("spam-check", e, "reading the contacts failed; checking without them"); return null; }
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
        return await MakeJunkRunner(account).ScanInboxAsync(rules, 200, default, lists.Effective, await SpamContactsAsync(account, lists));
    }

    /// <summary>What the Junk Cleaner's keywords and rules match in Junk Email, as a clean would see it (marked even while the cleaner is off).</summary>
    private async Task<JunkCleanerRunResult> ScanJunkForMarksAsync(ConnectedAccount account)
    {
        var lists = JunkListsStore.Load(account.AccountId);
        return await MakeJunkRunner(account).ScanAsync(JunkSettingsFor(account.AccountId) with { Enabled = true }, default, lists.Effective, await SpamContactsAsync(account, lists));
    }

    /// <summary>A clean leaves the Junk Email messages the user unticked.</summary>
    private JunkCleanerRunResult WithoutKeptJunk(string accountId, JunkCleanerRunResult scan)
    {
        var keep = _spamStore.For(accountId).KeepInJunk.ToHashSet(StringComparer.Ordinal);
        return keep.Count == 0 ? scan : scan with { Matched = scan.Matched.Where(h => !keep.Contains(h.MessageId)).ToList() };
    }

    /// <summary>The Junk Cleaner's settings (one account) or the shared keyword file (null = every account) changed: Junk Email's marks are worked out again.</summary>
    private void ForgetJunkMarks(string? accountId = null)
    {
        foreach (var key in _spamScans.Keys.Where(k => k.EndsWith("|junk", StringComparison.Ordinal) && (accountId is null || k == accountId + "|junk")).ToList())
            _spamScans.Remove(key);
        if (SpamTarget is { Junk: true } target && (accountId is null || target.Account.AccountId == accountId)) _ = RunSpamCheckAsync(target.Account, force: true, junk: true);
    }

    /// <summary>Ticking or unticking a message's box: an unticked suspect is remembered as not spam.</summary>
    private void SpamTickClicked(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not GraphMessageListRow row || SpamTarget is not { } target) return;
        SetSpamTick(target.Account, row, row.IsSpamTicked);
    }

    internal void SetSpamTick(ConnectedAccount account, GraphMessageListRow row, bool ticked)
    {
        row.IsSpamTicked = ticked;
        var junk = SpamTarget?.Junk == true;
        try { _spamStore.Save(InboxSpamCheckStore.WithNotSpam(_spamStore.For(account.AccountId), row.Message.Id, !ticked, junk)); }
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
        if (SpamTarget is not { } target || _activeMicrosoftFolder is not { } folder) return;
        var account = target.Account;
        var rows = TickedSpamRows();
        if (rows.Count == 0) return;
        if (!account.CanWriteMicrosoftMail)
        {
            await ExplainMailActionAsync("This saved Microsoft sign-in has read-only mail access. Sign in again to allow organizing mail.", account);
            return;
        }
        var scan = _spamScans.GetValueOrDefault(target.Key);
        var senders = rows.Select(r => JunkLists.Address(r.Message.FromAddress) ??
                (scan?.Suspects.TryGetValue(r.Message.Id, out var hit) == true ? JunkLists.Address(hit.Sender) : null))
            .OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (!await ConfirmSpamBlockAsync(senders, rows.Count)) return;
        if (senders.Count > 0)
        {
            var lists = JunkListsStore.Load(account.AccountId).WithAdded(JunkListKind.BlockedSenders, senders.ToArray());
            SaveJunkLists(lists);
        }
        AppLog.Note("spam-check", $"Block Sender and Delete in {target.Name} ({account.DisplayAddress}): {string.Join(", ", senders)}; {rows.Count} message(s) to Deleted Items");
        DeleteSpamRows(target, folder, rows);
        StatusText.Text = $"{(senders.Count == 1 ? senders[0] : $"{senders.Count} senders")} added to Blocked Senders; " +
            (rows.Count == 1 ? "1 message deleted." : $"{rows.Count} messages deleted.");
    }

    internal void DeleteTickedSpam()
    {
        if (SpamTarget is not { } target || _activeMicrosoftFolder is not { } folder) return;
        var rows = TickedSpamRows();
        if (rows.Count == 0) return;
        if (!target.Account.CanWriteMicrosoftMail)
        {
            _ = ExplainMailActionAsync("This saved Microsoft sign-in has read-only mail access. Sign in again to allow organizing mail.", target.Account);
            return;
        }
        AppLog.Note("spam-check", $"Delete in {target.Name} ({target.Account.DisplayAddress}): {rows.Count} message(s) to Deleted Items");
        DeleteSpamRows(target, folder, rows);
    }

    /// <summary>Moves the rows' messages to Deleted Items like the Delete button (the list changes at once; a refusal puts them back).</summary>
    private void DeleteSpamRows(SpamFolder target, MicrosoftFolderSelection folder, IReadOnlyList<GraphMessageListRow> rows)
    {
        var messages = rows.Select(r => r.Message).ToList();
        if (_spamScans.TryGetValue(target.Key, out var scan))
            foreach (var m in messages) scan.Suspects.Remove(m.Id);
        ActAtOnce(target.Account, folder, "delete", messages);
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
        if (sender is not Control button || SpamTarget is not { } target) return;
        var account = target.Account;
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
                Item("Check again", () => _ = RunSpamCheckAsync(account, force: true, target.Junk)),
            }
        };
        if (target.Junk) flyout.Items.Add(Item("Junk Cleaner rules...", () => _ = EditJunkCleanerRulesAsync(account)));
        else
        {
            flyout.Items.Add(Item("Inbox spam rules...", () => _ = EditSpamRulesAsync(account)));
            flyout.Items.Add(Item("Turn off the Inbox spam check", () => SetSpamCheckEnabled(account, false)));
        }
        flyout.ShowAt(button);
    }

    private void SetAllSpamTicks(ConnectedAccount account, bool ticked)
    {
        if (_graphRows is null) return;
        var settings = _spamStore.For(account.AccountId);
        var junk = SpamTarget?.Junk == true;
        foreach (var row in _graphRows.Where(r => r.IsSpamSuspect))
        {
            row.IsSpamTicked = ticked;
            settings = InboxSpamCheckStore.WithNotSpam(settings, row.Message.Id, !ticked, junk);
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
            if (SpamTarget is { Junk: false }) foreach (var row in _graphRows ?? []) row.SetSpam(null, false);
        }
        StatusText.Text = enabled ? $"The Inbox spam check is on for {account.DisplayAddress}." : $"The Inbox spam check is off for {account.DisplayAddress}. Turn it on again from Home > Junk.";
        AppLog.Note("spam-check", $"Inbox spam check {(enabled ? "on" : "off")} for {account.DisplayAddress}");
        if (enabled && SpamTarget is { Junk: false } t && t.Account.AccountId == account.AccountId) _ = RunSpamCheckAsync(account, force: true);
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

    /// <summary>The Junk Cleaner's own rules (the Other rules... window of Account Settings), from the bar in Junk Email.</summary>
    internal async Task EditJunkCleanerRulesAsync(ConnectedAccount account)
    {
        var junk = JunkSettingsFor(account.AccountId);
        var window = new JunkRulesWindow(account.DisplayAddress, junk.Rules, junk.Brands);
        await window.ShowDialog(this);
        if (window.Result is not { } result) return;
        SaveJunkSettings(junk with { Rules = result.Rules, Brands = result.Brands });
        AppLog.Note("spam-check", $"Junk Cleaner rules for {account.DisplayAddress}: {JunkRulesWindow.Summary(result.Rules)}");
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
