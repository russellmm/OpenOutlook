using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OpenOutlook.Auth;
using OpenOutlook.JunkCleaner;

namespace OpenOutlook.Desktop;

/// <summary>
/// The Inbox Cleaner tab of Account Settings (owner 2026-11-02): the Inbox spam check's on/off switch, keyword use and rules, next to the Junk
/// Cleaner tab, so both rule sets are set in one place. The rules are the account's own (<see cref="InboxSpamCheckStore"/>); the company list is
/// the Junk Cleaner's.
/// </summary>
public sealed partial class AccountSettingsWindow
{
    private readonly ComboBox _inboxAccount = new() { MinWidth = 260 };
    private readonly CheckBox _inboxEnabled = new() { Content = "Check the Inbox for spam (suspects get a tick in the message list; nothing moves until you click)" };
    private readonly CheckBox _inboxKeywords = new() { Content = "Also mark mail whose sender matches a Junk Cleaner keyword" };
    private readonly TextBlock _inboxRulesSummary = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private Button? _inboxRulesButton;
    private bool _inboxLoading;

    internal string InboxRulesSummaryText => _inboxRulesSummary.Text ?? "";

    private Control BuildInboxTab()
    {
        var gray = Themed.Brush("OlTextSecondary", Brushes.Gray);
        var rulesButton = Tool("Inbox rules…", "OlIconRemoveJunk", "Choose the rules that mark Inbox mail as suspected spam (separate from the Junk Cleaner's rules)");
        _inboxRulesButton = rulesButton;
        rulesButton.Click += async (_, _) => await EditInboxRulesAsync();
        _inboxAccount.SelectionChanged += (_, _) => LoadInbox();
        _inboxEnabled.IsCheckedChanged += (_, _) => SaveInbox();
        _inboxKeywords.IsCheckedChanged += (_, _) => SaveInbox();
        TextBlock Label(string t) => new() { Text = t, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 4), Foreground = Themed.Brush("OlText", Brushes.Black) };
        var intro = new TextBlock
        {
            Text = "Checks the newest 200 messages of the Inbox and ticks the ones that look like spam. Delete or Block Sender and Delete in the bar above the list removes the ticked ones; untick a message to keep it. Safe Senders are never marked. Microsoft accounts only.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = gray
        };
        var form = new StackPanel
        {
            Margin = new Thickness(10, 6, 10, 10), Spacing = 6,
            Children =
            {
                intro,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0), Children = { new TextBlock { Text = "Account", VerticalAlignment = VerticalAlignment.Center, FontSize = 13 }, _inboxAccount } },
                _inboxEnabled, _inboxKeywords,
                Label("Rules"), _inboxRulesSummary, new StackPanel { Orientation = Orientation.Horizontal, Children = { rulesButton } },
            }
        };
        return new ScrollViewer { Content = form, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    }

    private ConnectedAccount? SelectedInboxAccount() => (_inboxAccount.SelectedItem as ComboBoxItem)?.Tag as ConnectedAccount;

    private void RefreshInboxAccounts()
    {
        var selected = SelectedInboxAccount()?.AccountId;
        var accounts = _host.JunkAccounts();
        _inboxLoading = true;
        try
        {
            _inboxAccount.Items.Clear();
            foreach (var a in accounts) _inboxAccount.Items.Add(new ComboBoxItem { Content = a.DisplayAddress, Tag = a });
            _inboxAccount.SelectedIndex = accounts.Count == 0 ? -1 : Math.Max(0, accounts.ToList().FindIndex(a => a.AccountId == selected));
        }
        finally { _inboxLoading = false; }
        LoadInbox();
    }

    private void LoadInbox()
    {
        if (_inboxLoading) return;
        _inboxLoading = true;
        try
        {
            var account = SelectedInboxAccount();
            foreach (var c in new Control?[] { _inboxEnabled, _inboxKeywords, _inboxRulesButton }) if (c is not null) c.IsEnabled = account is not null;
            var s = account is null ? null : _host.InboxSpamSettings(account.AccountId);
            _inboxEnabled.IsChecked = s?.Enabled == true;
            _inboxKeywords.IsChecked = s?.UseKeywords == true;
            _inboxRulesSummary.Text = s is null ? "" : JunkRulesWindow.Summary(s.Rules);
        }
        finally { _inboxLoading = false; }
    }

    private void SaveInbox()
    {
        if (_inboxLoading || SelectedInboxAccount() is not { } a) return;
        var note = _host.SaveInboxSpamSettings(_host.InboxSpamSettings(a.AccountId) with { Enabled = _inboxEnabled.IsChecked == true, UseKeywords = _inboxKeywords.IsChecked == true });
        if (note is not null) _note.Text = note;
    }

    /// <summary>Opens the rules window for the Inbox; OK saves the rules at once (and a changed company list to the Junk Cleaner, which owns it).</summary>
    internal async Task EditInboxRulesAsync()
    {
        if (SelectedInboxAccount() is not { } a) { _note.Text = "Select an account first."; return; }
        var current = _host.InboxSpamSettings(a.AccountId);
        var junk = _host.JunkSettings(a.AccountId);
        var window = new JunkRulesWindow(a.DisplayAddress, current.Rules, junk.Brands, "Inbox spam rules");
        if (JunkRulesDialogOverride is { } answer) await answer(window);
        else await window.ShowDialog(this);
        if (window.Result is not { } result) return;
        if (!(result.Brands ?? []).SequenceEqual(junk.Brands ?? [])) _host.SaveJunkSettings(junk with { Brands = result.Brands });
        _note.Text = _host.SaveInboxSpamSettings(current with { Rules = result.Rules }) ?? "Inbox spam rules saved.";
        _inboxRulesSummary.Text = JunkRulesWindow.Summary(result.Rules);
        if (SelectedJunkAccount()?.AccountId == a.AccountId) LoadJunk();                 // the company list is shared with the Junk Cleaner tab
    }
}
