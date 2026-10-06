using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OpenOutlook.Auth;
using OpenOutlook.JunkCleaner;

namespace OpenOutlook.Desktop;

/// <summary>The Junk Cleaner tab of Account Settings: per-account keywords, rules, schedule, a Clean now command and the log of what was removed.</summary>
public sealed partial class AccountSettingsWindow
{
    private readonly ComboBox _junkAccount = new() { MinWidth = 260 };
    private readonly CheckBox _junkEnabled = new() { Content = "Use the Junk Cleaner for this account" };
    private readonly CheckBox _junkAuto = new() { Content = "Clean automatically in the background" };
    private readonly NumericUpDown _junkInterval = new() { Minimum = 1, Maximum = 60, Increment = 1, Value = 15, Width = 110, FormatString = "0" };
    private readonly TextBox _junkKeywords = new() { AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, MinHeight = 150, FontSize = 13 };
    private readonly CheckBox _junkHigh = new() { Content = "Also remove junk marked High importance (any sender)" };
    private readonly CheckBox _junkNoTo = new() { Content = "Also remove junk with no To address" };
    private readonly CheckBox _junkFlagged = new() { Content = "Also remove junk that is flagged" };
    private readonly CheckBox _junkBehalf = new() { Content = "Also remove junk sent \"on behalf of\" someone else" };
    private readonly TextBox _junkLog = new() { IsReadOnly = true, AcceptsReturn = true, FontSize = 12, MinHeight = 110, TextWrapping = TextWrapping.NoWrap };
    private bool _junkLoading;

    private Control BuildJunkTab()
    {
        var gray = Themed.Brush("OlTextSecondary", Brushes.Gray);
        var clean = Tool("Clean now…", "OlIconRemoveJunk", "Look through the Junk folder now and remove matching messages");
        var import = Tool("Import from OutlookJunkCleaner…", "OlIconOpenArchive", "Read the keywords and options from the old OutlookJunkCleaner config.json");
        clean.Click += async (_, _) =>
        {
            if (SelectedJunkAccount() is not { } a) { _note.Text = "Select an account first."; return; }
            SaveJunk();
            var note = await _host.CleanJunkNowAsync(a.AccountId, this);
            if (note is not null) _note.Text = note;
            RefreshJunkLog();
        };
        import.Click += async (_, _) =>
        {
            if (SelectedJunkAccount() is not { } a) { _note.Text = "Select an account first."; return; }
            var note = await _host.ImportJunkConfigAsync(a.AccountId, this);
            if (note is not null) _note.Text = note;
            LoadJunk();
        };
        _junkAccount.SelectionChanged += (_, _) => LoadJunk();
        _junkEnabled.IsCheckedChanged += (_, _) => SaveJunk();
        _junkAuto.IsCheckedChanged += (_, _) => SaveJunk();
        _junkHigh.IsCheckedChanged += (_, _) => SaveJunk();
        _junkNoTo.IsCheckedChanged += (_, _) => SaveJunk();
        _junkBehalf.IsCheckedChanged += (_, _) => SaveJunk();
        _junkFlagged.IsCheckedChanged += (_, _) => SaveJunk();
        _junkInterval.ValueChanged += (_, _) => SaveJunk();
        _junkKeywords.LostFocus += (_, _) => SaveJunk();
        TextBlock Label(string t) => new() { Text = t, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 4), Foreground = Themed.Brush("OlText", Brushes.Black) };
        var intro = new TextBlock
        {
            Text = "Removes messages from the Junk folder when the sender matches a keyword. Matches are moved to Deleted Items, never deleted for good, at most 500 per run. Microsoft accounts only.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = gray
        };
        var interval = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(24, 0, 0, 0),
            Children = { new TextBlock { Text = "Every", VerticalAlignment = VerticalAlignment.Center, FontSize = 13 }, _junkInterval, new TextBlock { Text = "minutes", VerticalAlignment = VerticalAlignment.Center, FontSize = 13 } }
        };
        var form = new StackPanel
        {
            Margin = new Thickness(10, 6, 10, 10), Spacing = 6,
            Children =
            {
                intro,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0), Children = { new TextBlock { Text = "Account", VerticalAlignment = VerticalAlignment.Center, FontSize = 13 }, _junkAccount } },
                _junkEnabled, _junkAuto, interval,
                Label("Remove junk whose From line contains (one keyword per line)"), _junkKeywords,
                Label("Other rules"), _junkHigh, _junkNoTo, _junkBehalf, _junkFlagged,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 8, 0, 0), Children = { clean, import } },
                Label("What was removed (newest first)"), _junkLog
            }
        };
        return new ScrollViewer { Content = form, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    }

    private ConnectedAccount? SelectedJunkAccount() => (_junkAccount.SelectedItem as ComboBoxItem)?.Tag as ConnectedAccount;

    private void RefreshJunkAccounts()
    {
        var selected = SelectedJunkAccount()?.AccountId;
        var accounts = _host.JunkAccounts();
        _junkLoading = true;
        try
        {
            _junkAccount.Items.Clear();
            foreach (var a in accounts) _junkAccount.Items.Add(new ComboBoxItem { Content = a.DisplayAddress, Tag = a });
            _junkAccount.SelectedIndex = accounts.Count == 0 ? -1 : Math.Max(0, accounts.ToList().FindIndex(a => a.AccountId == selected));
        }
        finally { _junkLoading = false; }
        LoadJunk();
    }

    private void LoadJunk()
    {
        if (_junkLoading) return;
        _junkLoading = true;
        try
        {
            var account = SelectedJunkAccount();
            foreach (var c in new Control[] { _junkEnabled, _junkAuto, _junkInterval, _junkKeywords, _junkHigh, _junkNoTo, _junkBehalf, _junkFlagged }) c.IsEnabled = account is not null;
            var s = account is null ? null : _host.JunkSettings(account.AccountId);
            _junkEnabled.IsChecked = s?.Enabled == true;
            _junkAuto.IsChecked = s?.AutoClean == true;
            _junkInterval.Value = s?.IntervalMinutes ?? 15;
            _junkKeywords.Text = s is null ? "" : string.Join(Environment.NewLine, s.Keywords);
            _junkHigh.IsChecked = s?.Rules.DeleteHighImportance == true;
            _junkNoTo.IsChecked = s?.Rules.DeleteMissingTo == true;
            _junkBehalf.IsChecked = s?.Rules.DeleteOnBehalfOf == true;
            _junkFlagged.IsChecked = s?.Rules.DeleteFlagged == true;
        }
        finally { _junkLoading = false; }
        RefreshJunkLog();
    }

    private void SaveJunk()
    {
        if (_junkLoading || SelectedJunkAccount() is not { } a) return;
        var keywords = (_junkKeywords.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(k => k.Trim()).Where(k => k.Length > 0).ToList();
        _host.SaveJunkSettings(new JunkCleanerAccountSettings
        {
            AccountId = a.AccountId, Enabled = _junkEnabled.IsChecked == true, AutoClean = _junkAuto.IsChecked == true,
            IntervalMinutes = Math.Clamp((int)(_junkInterval.Value ?? 15), 1, 60), Keywords = keywords,
            Rules = new JunkRuleOptions(_junkHigh.IsChecked == true, _junkNoTo.IsChecked == true, _junkBehalf.IsChecked == true, _junkFlagged.IsChecked == true)
        });
    }

    private void RefreshJunkLog()
    {
        var lines = _host.JunkLog();
        _junkLog.Text = lines.Count == 0 ? "Nothing has been removed yet." : string.Join(Environment.NewLine, lines);
    }
}
