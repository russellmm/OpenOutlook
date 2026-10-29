using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using OpenOutlook.Auth;
using OpenOutlook.JunkCleaner;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Desktop;

/// <summary>
/// Safe Senders, Safe Recipients and Blocked Senders (docs/junk-lists-plan.md): Outlook's lists read from its offline file at start, the
/// right-click / ribbon Junk menu (Block Sender, Never Block Sender / Domain / Group, Not Junk, Junk E-mail Options...) and the options window.
/// </summary>
public sealed partial class MainWindow
{
    private JunkListsStore? _junkListsStoreValue;
    internal JunkListsStore JunkListsStore => _junkListsStoreValue ??= new(Path.GetDirectoryName(new OpenOutlook.Mirror.MirrorSettingsStore().Path)!);

    /// <summary>Tests answer the Not Junk prompt here (null = Cancel, true / false = "Always trust" ticked or not).</summary>
    internal Func<string, Task<bool?>>? NotJunkPromptOverride { get; set; }

    private IReadOnlyList<ConnectedAccount> JunkListAccounts()
    {
        try { return _accountRegistry.Load().Where(a => a.Provider == OAuthProvider.MicrosoftConsumers).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return []; }
    }

    /// <summary>
    /// At start: Outlook's lists for the Microsoft accounts whose Junk Email Options say "Read Outlook's lists when OpenOutlook starts" (off by
    /// default, owner 2026-10-17), read when Outlook is closed and its file changed (in the background). "Read Outlook's lists now" always works.
    /// </summary>
    private void StartOutlookJunkListRead()
    {
        if (Environment.GetEnvironmentVariable("OPENOUTLOOK_NO_MIRROR") == "1" || !OperatingSystem.IsWindows()) return;
        var accounts = JunkListAccounts().Where(a => JunkListsStore.Load(a.AccountId).ReadOutlookAtStart).ToList();   // off unless ticked in Junk Email Options
        if (accounts.Count == 0) return;
        _ = Task.Run(() => { foreach (var account in accounts) ReadOutlookJunkLists(account, force: false); });
    }

    /// <summary>Reads Outlook's lists for one account into junk-lists.json. Returns the account's lists and what happened, in words.</summary>
    internal (JunkListsAccount Account, string Message) ReadOutlookJunkLists(ConnectedAccount account, bool force, string? outlookFolder = null, Func<bool>? outlookRunning = null)
    {
        var current = JunkListsStore.Load(account.AccountId);
        var outcome = OutlookJunkListReader.Read(account.DisplayAddress, current.OutlookFileStamp, force, outlookFolder, outlookRunning);
        if (outcome.Read is { } read)
        {
            var updated = current with
            {
                FromOutlook = read.Lists, OutlookOptions = read.Options, OutlookSource = read.Source,
                OutlookReadAt = DateTimeOffset.UtcNow, OutlookFileStamp = read.FileStamp,
            };
            try { JunkListsStore.Save(updated); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { AppLog.Error("junk-lists", e, "could not save junk-lists.json"); return (current, "The lists were read but could not be saved: " + e.Message); }
            var message = $"Read Outlook's lists for {account.DisplayAddress}: {read.Lists.SafeSenders.Count} safe senders, {read.Lists.SafeRecipients.Count} safe recipients, {read.Lists.BlockedSenders.Count} blocked senders ({read.Options.Level}).";
            AppLog.Note("junk-lists", message);
            return (updated, message);
        }
        if (outcome.Unchanged) return (current, "Outlook's lists have not changed since they were last read.");
        AppLog.Note("junk-lists", $"{account.DisplayAddress}: {outcome.Problem}");
        return (current, outcome.Problem ?? "");
    }

    internal JunkEmailOptionsWindow OpenJunkEmailOptions(ConnectedAccount account)
    {
        var window = new JunkEmailOptionsWindow(account.DisplayAddress, JunkListsStore.Load(account.AccountId), change => ChangeJunkLists(account, change),
            () => Task.Run(() => ReadOutlookJunkLists(account, force: true)));
        window.Show(this);
        return window;
    }

    /// <summary>Applies a change to the account's lists as they are stored now and saves it (the options window and the menu never overwrite each other).</summary>
    private JunkListsAccount ChangeJunkLists(ConnectedAccount account, Func<JunkListsAccount, JunkListsAccount> change)
    {
        var updated = change(JunkListsStore.Load(account.AccountId));
        SaveJunkLists(updated);
        AppLog.Note("junk-lists", $"Junk Email Options saved for {account.DisplayAddress}: {updated.Effective.SafeSenders.Count} safe senders, {updated.Effective.SafeRecipients.Count} safe recipients, {updated.Effective.BlockedSenders.Count} blocked senders, {updated.Effective.BlockedTopLevelDomains.Count} blocked top-level domains");
        _junkListLastRun.Remove(account.AccountId);                                                  // applied at the next minute's tick
        return updated;
    }

    /// <summary>Junk Email Options > Safe Senders > "Automatically add people I email": after a send from a Microsoft account, its recipients become safe senders.</summary>
    internal void AddSentRecipientsToSafeSenders(string fromAddress, IReadOnlyList<string> recipients)
    {
        var account = JunkListAccounts().FirstOrDefault(a => string.Equals(a.DisplayAddress, fromAddress, StringComparison.OrdinalIgnoreCase));
        if (account is null || recipients.Count == 0 || !JunkListsStore.Load(account.AccountId).AddRecipientsToSafeSenders) return;
        var stored = JunkListsStore.Load(account.AccountId);
        var addresses = recipients.Select(JunkLists.Address).OfType<string>().Where(a => !string.Equals(a, account.DisplayAddress, StringComparison.OrdinalIgnoreCase)).Distinct().ToArray();
        var added = addresses.Where(a => JunkLists.MatchingEntry(stored.Effective.SafeSenders, a) is null).ToArray();
        if (added.Length == 0) return;
        try
        {
            JunkListsStore.Save(stored.WithAdded(JunkListKind.SafeSenders, added));
            AppLog.Note("junk-lists", $"sent from {account.DisplayAddress}: {string.Join(", ", added)} added to Safe Senders (Automatically add people I email)");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AppLog.Error("junk-lists", e, "could not save junk-lists.json"); }
    }

    private void SaveJunkLists(JunkListsAccount account)
    {
        try { JunkListsStore.Save(account); StatusText.Text = "Junk email lists saved."; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { StatusText.Text = "The junk email lists could not be saved: " + e.Message; }
    }

    private bool InJunkEmailFolder => _activeMicrosoftFolder is { } f &&
        (f.Name.Equals("Junk Email", StringComparison.OrdinalIgnoreCase) || f.Id.Equals("junkemail", StringComparison.OrdinalIgnoreCase));

    /// <summary>Outlook's Junk submenu (right-click and Home > Junk). Items act on the selected Microsoft messages.</summary>
    private MenuItem JunkMenu()
    {
        MenuItem Item(string header, string action, string? icon = null)
        {
            var item = new MenuItem { Header = header, Tag = action, Icon = icon is null ? null : MenuIcon(icon) };
            item.Click += async (_, _) => await JunkActionAsync(action);
            return item;
        }
        var junk = new MenuItem { Header = "Junk" };
        var block = Item("_Block Sender", "block", "OlIconBlockSender");
        var never = Item("_Never Block Sender", "safeSender");
        var domain = Item("Never Block Sender's _Domain", "safeDomain");
        var group = Item("Never Block this Group or _Mailing List", "safeGroup");
        var notJunk = Item("N_ot Junk", "notJunk", "OlIconMail");
        var options = Item("Junk E-mail _Options...", "options", "OlIconJunkOptions");
        // Outlook's Report > Phishing and the Junk Cleaner's run, here since the separate Junk ribbon group went away (owner 2026-10-17)
        var phishing = new MenuItem { Header = "Report _Phishing", Tag = "phishing", Icon = MenuIcon("OlIconJunk"), IsEnabled = false };
        ToolTip.SetTip(phishing, "Planned feature");
        var clean = new MenuItem { Header = "_Clean Junk Folder...", Tag = "clean", Icon = MenuIcon("OlIconRemoveJunk") };
        ToolTip.SetTip(clean, "Remove messages that match your Junk Cleaner keywords and rules from the Junk Email folder (a preview first)");
        clean.Click += (s, a) => CleanJunkNowClicked(s, a);
        foreach (var x in new Control[] { block, never, domain, group, new Separator(), notJunk, options, new Separator(), phishing, clean, new Separator() }.Concat(SpamCheckMenuItems())) junk.Items.Add(x);
        junk.SubmenuOpened += (_, _) =>
        {
            var microsoft = _activeMicrosoftFolder is not null && MessageList.SelectedItems.OfType<GraphMessageListRow>().Any();
            block.IsEnabled = never.IsEnabled = domain.IsEnabled = group.IsEnabled = microsoft;
            notJunk.IsEnabled = microsoft && InJunkEmailFolder;
            options.IsEnabled = clean.IsEnabled = _activeMicrosoftFolder is not null || JunkListAccounts().Count > 0;
            var first = MessageList.SelectedItems.OfType<GraphMessageListRow>().FirstOrDefault()?.Message;
            domain.Header = first is not null && JunkLists.Address(first.FromAddress) is { } a ? $"Never Block Sender's _Domain ({a[a.IndexOf('@')..]})" : "Never Block Sender's _Domain";
        };
        return junk;
    }

    /// <summary>A ribbon icon (32 x 32 canvas) scaled into a 16 px menu or small-button slot.</summary>
    internal static Control MenuIcon(string key) => OlIcon.Create(OlIcon.Template(key), 16);

    /// <summary>Home > Clean Up: Outlook's three commands, not built yet (shown greyed, "Planned feature").</summary>
    private void HomeCleanUpClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control button) return;
        var flyout = new MenuFlyout();
        foreach (var header in new[] { "Clean Up Conversation", "Clean Up Folder", "Clean Up Folder & Subfolders" })
        {
            var item = new MenuItem { Header = header, IsEnabled = false };
            ToolTip.SetTip(item, "Planned feature");
            flyout.Items.Add(item);
        }
        flyout.ShowAt(button);
    }

    private void HomeJunkClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control button) return;
        var flyout = new MenuFlyout();
        var menu = JunkMenu();
        foreach (var item in menu.Items.OfType<Control>().ToList()) { menu.Items.Remove(item); flyout.Items.Add(item); }
        flyout.Items.Add(new Separator());
        var legacy = new MenuItem { Header = "Preview OutlookJunkCleaner settings..." };
        legacy.Click += (s, a) => PreviewJunkImportClicked(s, a);
        flyout.Items.Add(legacy);
        flyout.Opening += (_, _) =>
        {
            var microsoft = _activeMicrosoftFolder is not null && MessageList.SelectedItems.OfType<GraphMessageListRow>().Any();
            foreach (var item in flyout.Items.OfType<MenuItem>())
                item.IsEnabled = item.Tag switch
                {
                    "notJunk" => microsoft && InJunkEmailFolder,
                    "options" or "clean" or "spamCheck" or "spamRules" => _activeMicrosoftFolder is not null || JunkListAccounts().Count > 0,
                    "phishing" => false,
                    null => true,
                    _ => microsoft,
                };
        };
        flyout.ShowAt(button);
    }

    /// <summary>Block Sender, Never Block Sender / Domain / Group, Not Junk or Junk E-mail Options for the selected Microsoft messages.</summary>
    internal async Task JunkActionAsync(string action)
    {
        var folder = _activeMicrosoftFolder;
        if (action == "options")
        {
            var target = folder?.Account ?? JunkListAccounts().FirstOrDefault();
            if (target is null) { StatusText.Text = "Connect a Microsoft account to use its junk email lists."; return; }
            OpenJunkEmailOptions(target);
            return;
        }
        if (folder is null) { StatusText.Text = "Select Microsoft messages first."; return; }
        var account = folder.Account;
        var messages = MessageList.SelectedItems.OfType<GraphMessageListRow>().Select(r => r.Message).DistinctBy(m => m.Id).Take(100).ToList();
        if (messages.Count == 0) { StatusText.Text = "Select a message first."; return; }
        var writer = new GraphMailWriter(_graphHttp, account.AccountId);
        var addresses = new List<(GraphInboxMessage Message, string? From, IReadOnlyList<string> Recipients)>();
        try
        {
            foreach (var m in messages)
            {
                if (JunkLists.Address(m.FromAddress) is { } known && action is "block" or "safeSender" or "safeDomain" or "notJunk")
                { addresses.Add((m, known, [])); continue; }
                var (from, recipients) = await writer.GetAddressesAsync(await GetMicrosoftSession(account).GetAccessTokenAsync(), m.Id);
                addresses.Add((m, from, recipients));
            }
        }
        catch (Exception e) when (e is GraphMailException or HttpRequestException or ArgumentException or OperationCanceledException or InvalidOperationException)
        { StatusText.Text = "Could not read the sender of the selected message: " + e.Message; return; }

        var lists = JunkListsStore.Load(account.AccountId);
        var senders = addresses.Select(a => JunkLists.Address(a.From)).OfType<string>().Distinct().ToList();
        string Plural(int n, string one) => n == 1 ? one : $"{n} {one.Split(' ')[0]}s";
        switch (action)
        {
            case "block":
                if (senders.Count == 0) { StatusText.Text = "The selected message has no sender address."; return; }
                lists = lists.WithAdded(JunkListKind.BlockedSenders, senders.ToArray());
                SaveJunkLists(lists);
                if (!InJunkEmailFolder) MoveMicrosoftMessages(account, folder, messages, "junkemail");
                StatusText.Text = $"{string.Join(", ", senders.Take(3))}{(senders.Count > 3 ? "..." : "")} added to Blocked Senders" +
                    (InJunkEmailFolder ? "." : $"; {Plural(messages.Count, "message")} moved to Junk Email.");
                AppLog.Note("junk-lists", $"Block Sender ({account.DisplayAddress}): {string.Join(", ", senders)}" + (InJunkEmailFolder ? "" : $"; {messages.Count} message(s) moved to Junk Email"));
                break;
            case "safeSender":
                if (senders.Count == 0) { StatusText.Text = "The selected message has no sender address."; return; }
                SaveJunkLists(lists.WithAdded(JunkListKind.SafeSenders, senders.ToArray()));
                StatusText.Text = $"{string.Join(", ", senders.Take(3))}{(senders.Count > 3 ? "..." : "")} added to Safe Senders" + RescueFromJunk(account, folder, messages);
                AppLog.Note("junk-lists", $"Never Block Sender ({account.DisplayAddress}): {string.Join(", ", senders)}" + (InJunkEmailFolder ? $"; {messages.Count} message(s) moved to the Inbox" : ""));
                break;
            case "safeDomain":
                var domains = senders.Select(s => s[s.IndexOf('@')..]).Distinct().ToArray();
                if (domains.Length == 0) { StatusText.Text = "The selected message has no sender address."; return; }
                SaveJunkLists(lists.WithAdded(JunkListKind.SafeSenders, domains));
                StatusText.Text = $"{string.Join(", ", domains.Take(3))} added to Safe Senders" + RescueFromJunk(account, folder, messages);
                AppLog.Note("junk-lists", $"Never Block Sender's Domain ({account.DisplayAddress}): {string.Join(", ", domains)}" + (InJunkEmailFolder ? $"; {messages.Count} message(s) moved to the Inbox" : ""));
                break;
            case "safeGroup":
                var own = account.DisplayAddress.Trim().ToLowerInvariant();
                var groups = addresses.SelectMany(a => a.Recipients).Select(JunkLists.Address).OfType<string>().Where(r => r != own).Distinct().ToArray();
                if (groups.Length == 0) { StatusText.Text = "The message was sent only to you, so there is no group or mailing list to add."; return; }
                SaveJunkLists(lists.WithAdded(JunkListKind.SafeRecipients, groups));
                StatusText.Text = $"{string.Join(", ", groups.Take(3))}{(groups.Length > 3 ? "..." : "")} added to Safe Recipients" + RescueFromJunk(account, folder, messages);
                AppLog.Note("junk-lists", $"Never Block this Group ({account.DisplayAddress}): {string.Join(", ", groups)}" + (InJunkEmailFolder ? $"; {messages.Count} message(s) moved to the Inbox" : ""));
                break;
            case "notJunk":
                if (!InJunkEmailFolder) { StatusText.Text = "Not Junk works on messages in the Junk Email folder."; return; }
                var trust = await AskNotJunkAsync(senders);
                if (trust is null) return;
                if (trust == true && senders.Count > 0) SaveJunkLists(lists.WithAdded(JunkListKind.SafeSenders, senders.ToArray()));
                MoveMicrosoftMessages(account, folder, messages, "inbox");
                StatusText.Text = $"{Plural(messages.Count, "message")} moved to the Inbox" + (trust == true && senders.Count > 0 ? "; sender added to Safe Senders." : ".");
                AppLog.Note("junk-lists", $"Not Junk ({account.DisplayAddress}): {messages.Count} message(s) moved to the Inbox from {string.Join(", ", senders)}" + (trust == true && senders.Count > 0 ? "; added to Safe Senders" : ""));
                break;
        }
    }

    /// <summary>
    /// After Never Block Sender / Domain / Group in Junk Email: the selected messages go to the Inbox now (Outlook leaves them; the owner expected
    /// them to move, 2026-10-17), and the next minute's list run moves the sender's other mail out of Junk Email. Returns the status-line tail.
    /// </summary>
    private string RescueFromJunk(ConnectedAccount account, MicrosoftFolderSelection folder, IReadOnlyList<GraphInboxMessage> messages)
    {
        _junkListLastRun.Remove(account.AccountId);
        if (!InJunkEmailFolder) return ".";
        MoveMicrosoftMessages(account, folder, messages, "inbox");
        return messages.Count == 1 ? "; the message was moved to the Inbox." : $"; {messages.Count} messages were moved to the Inbox.";
    }

    /// <summary>Takes the messages off the list at once and moves them in the background (one at a time, in order); a refusal puts them back.</summary>
    private void MoveMicrosoftMessages(ConnectedAccount account, MicrosoftFolderSelection folder, IReadOnlyList<GraphInboxMessage> messages, string destination)
    {
        foreach (var m in messages) ApplyCompletedMailAction(m, "archive");                      // "archive" = the row leaves this folder
        _ = Task.Run(async () =>
        {
            await _msActionGate.WaitAsync();
            var writer = new GraphMailWriter(_graphHttp, account.AccountId);
            var done = 0;
            try
            {
                foreach (var m in messages)
                {
                    await writer.MoveAsync(await GetMicrosoftSession(account).GetAccessTokenAsync(), m.Id, destination);
                    done++;
                }
                _ = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => RefreshMicrosoftFolderCountsAsync(account, CancellationToken.None, force: true));
                AppLog.Note("junk-lists", $"{done} message(s) moved to {(destination == "inbox" ? "the Inbox" : "Junk Email")}");
            }
            catch (Exception error) when (error is GraphMailException or HttpRequestException or ArgumentException or OperationCanceledException or InvalidOperationException)
            {
                var failed = messages.Skip(done).Select(m => m.Id).ToArray();
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    UndoLocalChange(account, failed);
                    AppLog.Error("junk-lists", error, $"moving {failed.Length} message(s) to {destination} failed");
                    StatusText.Text = "Microsoft did not accept the move: " + error.Message;
                    if (_activeMicrosoftFolder == folder) await RefreshMicrosoftFolderAsync();
                });
            }
            finally { _msActionGate.Release(); }
        });
    }

    /// <summary>Outlook's "Mark as Not Junk" prompt: the message goes back to the Inbox; "Always trust email from ..." adds the sender to Safe Senders.</summary>
    private async Task<bool?> AskNotJunkAsync(IReadOnlyList<string> senders)
    {
        var who = senders.Count == 1 ? $"\"{senders[0]}\"" : senders.Count == 0 ? "the sender" : $"these {senders.Count} senders";
        if (NotJunkPromptOverride is not null) return await NotJunkPromptOverride(who);
        var trust = new CheckBox { Content = $"Always trust email from {who}", IsChecked = senders.Count > 0, IsEnabled = senders.Count > 0 };
        var dialog = new Window
        {
            Title = "Mark as Not Junk", Width = 460, Height = 190, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Themed.Brush("OlCard", Brushes.White),
        };
        var ok = new Button { Content = "OK", MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true };
        ok.Click += (_, _) => dialog.Close(trust.IsChecked == true);
        cancel.Click += (_, _) => dialog.Close(null);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16), Spacing = 12, Children =
            {
                new TextBlock { Text = "This message will be moved back into the Inbox folder.", TextWrapping = TextWrapping.Wrap },
                trust,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
            }
        };
        return await dialog.ShowDialog<bool?>(this);
    }
}
