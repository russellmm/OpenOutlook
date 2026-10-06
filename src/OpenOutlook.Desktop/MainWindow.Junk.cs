using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OpenOutlook.Auth;
using OpenOutlook.JunkCleaner;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Desktop;

/// <summary>The Junk Cleaner: settings, the manual "Clean Junk Now" with a preview, and the silent scheduled run (Microsoft accounts).</summary>
public partial class MainWindow
{
    private readonly JunkCleanerSettingsStore _junkStore = new(Path.GetDirectoryName(new OpenOutlook.Mirror.MirrorSettingsStore().Path)!);
    private readonly JunkCleanerLog _junkLog = new(Path.Combine(Path.GetDirectoryName(new OpenOutlook.Mirror.MirrorSettingsStore().Path)!, "junk-cleaner.log"));
    private readonly Dictionary<string, DateTime> _junkLastRun = new(StringComparer.Ordinal);
    private DispatcherTimer? _junkTimer;
    private bool _junkBusy;

    private IReadOnlyList<ConnectedAccount> JunkAccounts()
    {
        try { return _accountRegistry.Load().Where(a => a.Provider == OAuthProvider.MicrosoftConsumers).ToList(); }
        catch (Exception) { return []; }
    }

    private JunkCleanerAccountSettings JunkSettingsFor(string accountId)
    {
        try { return _junkStore.Load().Accounts.FirstOrDefault(a => a.AccountId == accountId) ?? new JunkCleanerAccountSettings { AccountId = accountId }; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { return new JunkCleanerAccountSettings { AccountId = accountId }; }
    }

    private void SaveJunkSettings(JunkCleanerAccountSettings settings)
    {
        try
        {
            var all = _junkStore.Load().Accounts.Where(a => a.AccountId != settings.AccountId).Append(settings).ToArray();
            _junkStore.Save(new JunkCleanerSettings { Accounts = all });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        { StatusText.Text = "Could not save the Junk Cleaner settings: " + e.Message; }
    }

    private MicrosoftJunkCleanerRunner MakeJunkRunner(ConnectedAccount account)
    {
        var session = GetMicrosoftSession(account);
        var reader = new GraphJunkMailReader(_graphHttp, async ct => await session.GetAccessTokenAsync(ct), account.AccountId);
        return new MicrosoftJunkCleanerRunner(reader, new GraphMailWriter(_graphHttp, account.AccountId), ct => session.GetAccessTokenAsync(ct), account.AccountId);
    }

    /// <summary>Why this account cannot be cleaned right now, or null when it can.</summary>
    private static string? JunkProblem(ConnectedAccount account, JunkCleanerAccountSettings s) =>
        !account.CanWriteMicrosoftMail ? "This sign-in has read-only mail access. Sign in again from Account Settings to allow organising mail."
        : !s.Enabled ? "The Junk Cleaner is turned off for this account. Turn it on in Account Settings > Junk Cleaner."
        : s.Keywords.Count == 0 && !s.Rules.DeleteHighImportance && !s.Rules.DeleteMissingTo && !s.Rules.DeleteOnBehalfOf && !s.Rules.DeleteFlagged ? "Add at least one keyword or rule in Account Settings > Junk Cleaner first."
        : null;

    /// <summary>The manual run: scan, show what would be removed, remove what the user keeps ticked.</summary>
    private async Task<string?> CleanJunkNowAsync(ConnectedAccount account, Window owner)
    {
        var settings = JunkSettingsFor(account.AccountId);
        if (JunkProblem(account, settings) is { } problem) return problem;
        if (_junkBusy) return "The Junk Cleaner is already running.";
        _junkBusy = true;
        try
        {
            StatusText.Text = $"Looking through the Junk folder of {account.DisplayAddress}…";
            var runner = MakeJunkRunner(account);
            var scan = await runner.ScanAsync(settings);
            if (scan.Matched.Count == 0) return $"Nothing in the Junk folder matches ({scan.Scanned} messages looked at).";
            var dialog = new JunkPreviewDialog(account.DisplayAddress, scan);
            await dialog.ShowDialog(owner);
            if (dialog.Chosen is not { Count: > 0 } chosen) return "Nothing was removed.";
            StatusText.Text = $"Moving {chosen.Count} junk messages to Deleted Items…";
            var result = await runner.MoveAsync(scan, chosen);
            _junkLog.Append(account.DisplayAddress, result, scheduled: false);
            _ = AfterJunkRunAsync(account, result);
            return Summary(result);
        }
        catch (Exception e) when (e is GraphMailException or HttpRequestException or ArgumentException or OperationCanceledException)
        { return "The Junk Cleaner could not finish: " + e.Message; }
        finally { _junkBusy = false; }
    }

    private static string Summary(JunkCleanerRunResult r) =>
        $"Moved {r.Moved.Count} message{(r.Moved.Count == 1 ? "" : "s")} from Junk to Deleted Items" +
        (r.Failures.Count > 0 ? $"; {r.Failures.Count} problem{(r.Failures.Count == 1 ? "" : "s")} (see the log)" : "") + (r.CapReached ? "; more matches remain, the next run continues" : "") + ".";

    /// <summary>The mailbox copy is refreshed so Junk and Deleted Items show the change.</summary>
    private async Task AfterJunkRunAsync(ConnectedAccount account, JunkCleanerRunResult result)
    {
        if (result.Moved.Count == 0) return;
        StatusText.Text = Summary(result);
        if (Environment.GetEnvironmentVariable("OPENOUTLOOK_NO_MIRROR") != "1") await SyncMirrorAsync(account, manual: false);
        if (_activeMicrosoftFolder?.Account.AccountId == account.AccountId) await RefreshMicrosoftFolderAsync();
    }

    private async Task<string?> ImportJunkConfigAsync(string accountId, Window owner)
    {
        var files = await SafePick.FilesAsync(owner, new FilePickerOpenOptions
        {
            Title = "Choose the OutlookJunkCleaner config.json", AllowMultiple = false,
            SuggestedStartLocation = null, FileTypeFilter = [new FilePickerFileType("JSON configuration") { Patterns = ["*.json"] }]
        }, failure => StatusText.Text = failure);
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return null;
        try
        {
            var preview = await Task.Run(() => JunkCleanerSettingsStore.PreviewLegacyConfig(path));
            var current = JunkSettingsFor(accountId);
            var imported = preview.ImportForAccount(accountId);
            SaveJunkSettings(current with
            {
                Keywords = JunkCleanerSettingsStore.NormalizeKeywords(current.Keywords.Concat(imported.Keywords)),
                Rules = new JunkRuleOptions(current.Rules.DeleteHighImportance || imported.Rules.DeleteHighImportance,
                    current.Rules.DeleteMissingTo || imported.Rules.DeleteMissingTo, current.Rules.DeleteOnBehalfOf || imported.Rules.DeleteOnBehalfOf, current.Rules.DeleteFlagged),
                IntervalMinutes = imported.IntervalMinutes, Enabled = true            // automatic cleaning stays as it was; turn it on deliberately
            });
            return $"Imported {preview.Keywords.Count} keywords. The Junk Cleaner is on for this account; automatic cleaning is unchanged.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { return "Could not read that configuration file."; }
    }

    // ---- ribbon ----------------------------------------------------------------------------------------------------------------------------------

    private async void CleanJunkNowClicked(object? sender, RoutedEventArgs e)
    {
        var accounts = JunkAccounts();
        var account = _activeMicrosoftAccount is { } active && accounts.Any(a => a.AccountId == active.AccountId) ? active
            : accounts.FirstOrDefault(a => a.AccountId == _accountDefault.Load()) ?? accounts.FirstOrDefault();
        if (account is null) { StatusText.Text = "The Junk Cleaner works with a connected Microsoft account. Add one in Account Settings."; return; }
        if (JunkProblem(account, JunkSettingsFor(account.AccountId)) is { } problem)
        {
            StatusText.Text = problem;
            ShowAccountSettings(junk: true);
            return;
        }
        StatusText.Text = await CleanJunkNowAsync(account, this) ?? "";
    }

    // ---- scheduled run ---------------------------------------------------------------------------------------------------------------------------

    /// <summary>At start-up: remember account checks between calls, and get a token and an open connection for each Microsoft account so the first folder click is not the slow one.</summary>
    private async Task WarmUpMicrosoftAsync()
    {
        if (Environment.GetEnvironmentVariable("OPENOUTLOOK_NO_MIRROR") == "1") return;
        GraphAccountVerification.CacheEnabled = true;
        foreach (var account in MirrorAccounts().Where(a => a.Provider == OAuthProvider.MicrosoftConsumers))
        {
            try
            {
                var token = await GetMicrosoftSession(account).GetAccessTokenAsync();
                await GraphAccountVerification.WarmUpAsync(_graphHttp, account.AccountId, token);
            }
            catch (Exception) { }                                                      // warming up is optional
        }
    }

    private void StartJunkScheduler()
    {
        if (Environment.GetEnvironmentVariable("OPENOUTLOOK_NO_MIRROR") == "1") return;     // tests must not touch real mailboxes
        _junkTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _junkTimer.Tick += async (_, _) => await RunDueJunkCleansAsync();
        _junkTimer.Start();
    }

    /// <summary>Silently cleans every account whose interval has passed. Never throws; problems go to the log.</summary>
    internal async Task RunDueJunkCleansAsync()
    {
        if (_junkBusy) return;
        foreach (var account in JunkAccounts())
        {
            var settings = JunkSettingsFor(account.AccountId);
            if (!settings.AutoClean || JunkProblem(account, settings) is not null) continue;
            if (_junkLastRun.TryGetValue(account.AccountId, out var last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(settings.IntervalMinutes)) continue;
            _junkLastRun[account.AccountId] = DateTime.UtcNow;
            _junkBusy = true;
            try
            {
                var result = await MakeJunkRunner(account).CleanAsync(settings);
                if (result.Moved.Count > 0 || result.Failures.Count > 0) _junkLog.Append(account.DisplayAddress, result, scheduled: true);
                if (result.Moved.Count > 0) await AfterJunkRunAsync(account, result);
            }
            catch (Exception e) when (e is GraphMailException or HttpRequestException or ArgumentException or OperationCanceledException or IOException)
            { AppLog.Error("junk-cleaner", e, "scheduled junk clean failed"); }
            finally { _junkBusy = false; }
        }
    }
}

/// <summary>Lists what a manual run would remove, each with a tick box; Chosen holds the ticked messages after "Move to Deleted Items".</summary>
public sealed class JunkPreviewDialog : Window
{
    public IReadOnlyList<JunkCleanerHit>? Chosen { get; private set; }

    public JunkPreviewDialog(string address, JunkCleanerRunResult scan)
    {
        Title = "Clean Junk Folder";
        Width = 760; Height = 520; MinWidth = 520; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Themed.Brush("OlCard", Brushes.White);
        var boxes = new List<(CheckBox Box, JunkCleanerHit Hit)>();
        var list = new StackPanel();
        foreach (var hit in scan.Matched)
        {
            var box = new CheckBox
            {
                IsChecked = true, Margin = new Thickness(6, 2),
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = hit.Subject, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = $"{hit.Sender}   ·   {string.Join(", ", hit.Reasons)}", FontSize = 12, Foreground = Themed.Brush("OlTextSecondary", Brushes.Gray), TextTrimming = TextTrimming.CharacterEllipsis }
                    }
                }
            };
            boxes.Add((box, hit));
            list.Children.Add(box);
        }
        var cap = scan.CapReached ? $" Only the first {MicrosoftJunkCleanerRunner.MaxMovesPerRun} are moved in one run." : "";
        var header = new TextBlock
        {
            Margin = new Thickness(20, 16, 20, 8), TextWrapping = TextWrapping.Wrap, FontSize = 13,
            Text = $"{scan.Matched.Count} of {scan.Scanned} messages in the Junk folder of {address} match. Untick any you want to keep. They are moved to Deleted Items, not deleted for good.{cap}"
        };
        var move = new Button { Content = "Move to Deleted Items", Padding = new Thickness(18, 6), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(18, 6), IsCancel = true };
        move.Click += (_, _) => { Chosen = boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Hit).ToList(); Close(); };
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 10), Children = { move, cancel } };
        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top); DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(header); dock.Children.Add(buttons);
        dock.Children.Add(new Border { BorderBrush = Themed.Brush("OlHairline", Brushes.LightGray), BorderThickness = new Thickness(0, 1), Child = new ScrollViewer { Content = list } });
        Content = dock;
    }
}
