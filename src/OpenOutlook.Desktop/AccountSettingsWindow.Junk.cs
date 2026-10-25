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
    private readonly TextBox _junkKeywords = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 110, MaxHeight = 260, FontSize = 13, Watermark = "casino, aarp, free spins" };
    private Button? _junkSaveKeywords;
    private string _junkKeywordsSaved = "";
    private readonly TextBlock _junkKeywordInfo = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    // The rules besides the keywords live in the "Other rules..." window (JunkRulesWindow); the tab shows which are on.
    private readonly TextBlock _junkRulesSummary = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private Button? _junkRulesButton;
    private JunkRuleOptions _junkRules;
    private IReadOnlyList<string>? _junkBrands;

    /// <summary>Set in tests: answers the "Other rules..." window instead of showing it.</summary>
    internal Func<JunkRulesWindow, Task>? JunkRulesDialogOverride { get; set; }
    internal string JunkRulesSummaryText => _junkRulesSummary.Text ?? "";
    private readonly TextBox _junkLog = new() { IsReadOnly = true, AcceptsReturn = true, FontSize = 12, MinHeight = 110, TextWrapping = TextWrapping.NoWrap };
    private bool _junkLoading;

    private Control BuildJunkTab()
    {
        var gray = Themed.Brush("OlTextSecondary", Brushes.Gray);
        var clean = Tool("Clean now…", "OlIconRemoveJunk", "Look through the Junk folder now and remove matching messages");
        var import = Tool("Import keywords…", "OlIconOpenArchive", "Add the keywords and options from a file: one saved with Export, or the old OutlookJunkCleaner config.json");
        var openFile = Tool("Open keyword file", "OlIconOpenArchive", "Open junk-keywords.json in your text editor: one comma-separated line of keywords, shared by all accounts. Click Reload afterwards");
        var reload = Tool("Reload", "OlIconRefresh", "Throw away unsaved changes and read the keyword file again");
        var saveKeywords = Tool("Save keywords", "OlIconSave", "Write the keywords above to junk-keywords.json");
        _junkSaveKeywords = saveKeywords;
        saveKeywords.IsEnabled = false;
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
        openFile.Click += (_, _) =>
        {
            var path = _host.JunkKeywordsPath();
            _note.Text = AttachmentLauncher.Launch(path) ?? "Edit the line of keywords, save the file, then click Reload.";
        };
        reload.Click += (_, _) => { LoadJunk(); _note.Text = "Keywords reloaded."; };
        saveKeywords.Click += (_, _) =>
        {
            var note = _host.SaveJunkKeywords(ParseJunkKeywords(_junkKeywords.Text));
            LoadJunk();
            _note.Text = note;
        };
        var rulesButton = Tool("Other rules…", "OlIconRemoveJunk", "Choose the rules besides the keywords: sender checks, recipients, subject and message rules");
        _junkRulesButton = rulesButton;
        rulesButton.Click += async (_, _) => await EditJunkRulesAsync();
        _junkKeywords.TextChanged += (_, _) => { if (!_junkLoading) saveKeywords.IsEnabled = _junkKeywords.Text != _junkKeywordsSaved; };
        _junkAccount.SelectionChanged += (_, _) => LoadJunk();
        _junkEnabled.IsCheckedChanged += (_, _) => SaveJunk();
        _junkAuto.IsCheckedChanged += (_, _) => SaveJunk();
        _junkInterval.ValueChanged += (_, _) => SaveJunk();
        TextBlock Label(string t) => new() { Text = t, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 4), Foreground = Themed.Brush("OlText", Brushes.Black) };
        var intro = new TextBlock
        {
            Text = "Removes messages from the Junk folder when the sender matches a keyword or the message matches one of the other rules. Matches are moved to Deleted Items, never deleted for good, at most 500 per run. Microsoft accounts only.",
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
                Label("Remove junk whose From line contains any of these keywords (separate them with commas)"), _junkKeywordInfo, _junkKeywords,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { saveKeywords, reload, openFile } },
                Label("Other rules"), _junkRulesSummary, new StackPanel { Orientation = Orientation.Horizontal, Children = { rulesButton } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 8, 0, 0), Children = { clean, import } },
                Label($"What was removed (newest first; the history keeps the latest {JunkCleanerLog.MaxEntries:N0} entries)"), _junkLog
            }
        };
        return new ScrollViewer { Content = form, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    }

    /// <summary>The words of the keyword box: separated by commas (new lines and semicolons work too), trimmed, without duplicates.</summary>
    internal static IReadOnlyList<string> ParseJunkKeywords(string? text) =>
        JunkCleanerSettingsStore.NormalizeKeywords((text ?? "").Split(new[] { ',', '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries));

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
            foreach (var c in new Control[] { _junkEnabled, _junkAuto, _junkInterval, _junkKeywords }) c.IsEnabled = account is not null;
            var s = account is null ? null : _host.JunkSettings(account.AccountId);
            _junkEnabled.IsChecked = s?.Enabled == true;
            _junkAuto.IsChecked = s?.AutoClean == true;
            _junkInterval.Value = s?.IntervalMinutes ?? 15;
            _junkKeywordsSaved = s is null ? "" : string.Join(", ", s.Keywords);
            _junkKeywords.Text = _junkKeywordsSaved;
            if (_junkSaveKeywords is not null) _junkSaveKeywords.IsEnabled = false;
            _junkKeywordInfo.Text = s is null ? "" : $"{s.Keywords.Count} keyword{(s.Keywords.Count == 1 ? "" : "s")} in {_host.JunkKeywordsPath()}";
            if (_junkRulesButton is not null) _junkRulesButton.IsEnabled = account is not null;
            _junkRules = s?.Rules ?? new JunkRuleOptions();
            _junkBrands = s?.Brands;
            _junkRulesSummary.Text = account is null ? "" : JunkRulesWindow.Summary(_junkRules);
        }
        finally { _junkLoading = false; }
        RefreshJunkLog();
    }

    private void SaveJunk()
    {
        if (_junkLoading || SelectedJunkAccount() is not { } a) return;
        var keywords = _host.JunkSettings(a.AccountId).Keywords;                       // the keywords are edited in the file, not here
        _host.SaveJunkSettings(new JunkCleanerAccountSettings
        {
            AccountId = a.AccountId, Enabled = _junkEnabled.IsChecked == true, AutoClean = _junkAuto.IsChecked == true,
            IntervalMinutes = Math.Clamp((int)(_junkInterval.Value ?? 15), 1, 60), Keywords = keywords,
            Rules = _junkRules, Brands = _junkBrands
        });
    }

    /// <summary>Opens "Other rules..."; OK saves the chosen rules for the selected account at once.</summary>
    internal async Task EditJunkRulesAsync()
    {
        if (SelectedJunkAccount() is not { } a) { _note.Text = "Select an account first."; return; }
        var window = new JunkRulesWindow(a.DisplayAddress, _junkRules, _junkBrands);
        if (JunkRulesDialogOverride is { } answer) await answer(window);
        else await window.ShowDialog(this);
        if (window.Result is not { } result) return;
        _junkRules = result.Rules;
        _junkBrands = result.Brands;
        SaveJunk();
        _junkRulesSummary.Text = JunkRulesWindow.Summary(_junkRules);
        _note.Text = "Junk Cleaner rules saved.";
    }

    private void RefreshJunkLog()
    {
        var lines = _host.JunkLog();
        _junkLog.Text = lines.Count == 0 ? "Nothing has been removed yet." : string.Join(Environment.NewLine, lines);
    }
}
