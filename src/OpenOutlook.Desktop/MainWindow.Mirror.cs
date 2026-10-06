using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OpenOutlook.Auth;
using OpenOutlook.Mirror;
using OpenOutlook.PstNative;
using PstCore;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Desktop;

/// <summary>
/// The local copy of each Microsoft mailbox: a PST file per account (default folder or the owner's choice), kept up to date in the background and
/// shown in the folder tree like any archive, so reading is from the file. Changes made in the copy (read, flag, move, delete, new folders) are sent to the server on the next sync; while offline they wait.
/// </summary>
public partial class MainWindow
{
    private readonly MirrorSettingsStore _mirrorSettings = new();
    private readonly Dictionary<string, string> _mirrorStatus = new(StringComparer.Ordinal);
    private DispatcherTimer? _mirrorTimer;
    private DispatcherTimer? _mirrorWatchTimer;
    private readonly Dictionary<string, DateTime> _mirrorLastSync = new(StringComparer.Ordinal);       // when each copy was last synchronised (UTC)
    private bool _mirrorBusy;
    private readonly Dictionary<string, MirrorInfo> _mirrorInfo = new(StringComparer.Ordinal);       // what the status bar indicator shows, per account

    /// <summary>Redraws the sync indicator at the right of the status bar from what is known about every mailbox copy.</summary>
    private void UpdateSyncIndicator()
    {
        var state = SyncIndicatorState.Compute(_mirrorInfo.Values.ToList(), DateTime.UtcNow);
        SyncIndicator.IsVisible = state.Visible;
        SyncIndicatorGlyph.Text = state.Glyph;
        SyncIndicatorText.Text = state.Text;
        SyncIndicatorGlyph.Foreground = state.Phase switch
        {
            SyncPhase.Problem => Avalonia.Media.Brushes.Firebrick,
            SyncPhase.Offline => Avalonia.Media.Brushes.DarkOrange,
            SyncPhase.Idle => Avalonia.Media.Brushes.SeaGreen,
            _ => Avalonia.Media.Brushes.SteelBlue
        };
        ToolTip.SetTip(SyncIndicator, state.Tooltip);
    }

    private void SyncIndicatorClicked(object? sender, RoutedEventArgs e) => ShowAccountSettings(dataFiles: true);

    private void SetMirrorInfo(ConnectedAccount account, SyncPhase phase, string detail = "", int waiting = 0, bool keepLast = true)
    {
        _mirrorInfo.TryGetValue(account.AccountId, out var old);
        _mirrorInfo[account.AccountId] = new MirrorInfo(account.DisplayAddress, phase, detail, keepLast ? old?.LastSyncUtc : null, waiting);
        UpdateSyncIndicator();
    }

    private void StartMirrorScheduler()
    {
        if (Environment.GetEnvironmentVariable("OPENOUTLOOK_NO_MIRROR") == "1") return;     // tests and tools that must not touch real mailboxes
        _mirrorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _mirrorTimer.Tick += async (_, _) =>
        {
            _mirrorTimer!.Interval = TimeSpan.FromMinutes(15);                  // the first run follows start-up by 20 s, then every 15 minutes
            await SyncAllMirrorsAsync(manual: false);
        };
        _mirrorTimer.Start();
        // reading, flagging, moving or deleting in the copy writes the file: such a change is sent to the server within about half a minute
        _mirrorWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _mirrorWatchTimer.Tick += async (_, _) =>
        {
            UpdateSyncIndicator();                                                    // keeps "3 min ago" current
            if (_mirrorBusy) return;
            foreach (var account in MirrorAccounts())
            {
                var path = MirrorPathFor(account);
                if (!_mirrorLastSync.TryGetValue(account.AccountId, out var last) || !File.Exists(path)) continue;
                if (File.GetLastWriteTimeUtc(path) > last.AddSeconds(5)) { await SyncMirrorAsync(account, manual: false); break; }
            }
        };
        _mirrorWatchTimer.Start();
    }

    private IEnumerable<ConnectedAccount> MirrorAccounts()
    {
        try { return _accountRegistry.Load().Where(a => a.Provider is OAuthProvider.MicrosoftConsumers or OAuthProvider.Google).ToList(); }
        catch (Exception) { return []; }
    }

    private string MirrorPathFor(ConnectedAccount account) => MirrorLocations.PstPathFor(_mirrorSettings.Load(), account.AccountId, account.DisplayAddress);

    private async Task SyncAllMirrorsAsync(bool manual)
    {
        foreach (var account in MirrorAccounts()) await SyncMirrorAsync(account, manual);
    }

    /// <summary>Brings one account's mailbox copy up to date. Never throws: problems become the status shown in Data Files and the status bar.</summary>
    private async Task SyncMirrorAsync(ConnectedAccount account, bool manual)
    {
        if (_mirrorBusy) { if (manual) StatusText.Text = "A mailbox copy is already being updated."; return; }
        var settings = _mirrorSettings.Load();
        var options = settings.For(account.AccountId);
        if (!options.Enabled) { _mirrorStatus[account.AccountId] = "turned off"; SetMirrorInfo(account, SyncPhase.Off); return; }
        _mirrorBusy = true;
        var syncStarted = DateTime.UtcNow;
        var path = MirrorLocations.PstPathFor(settings, account.AccountId, account.DisplayAddress);
        try
        {
            var check = MirrorLocations.Check(System.IO.Path.GetDirectoryName(path)!);
            if (!check.Ok) { _mirrorStatus[account.AccountId] = "the folder cannot be used: " + check.Problem; SetMirrorInfo(account, SyncPhase.Problem, "the folder cannot be used: " + check.Problem); StatusText.Text = $"The mailbox copy folder of {account.DisplayAddress} cannot be used ({check.Problem}). Change it in Data Files."; return; }
            _mirrorStatus[account.AccountId] = "synchronising…";
            SetMirrorInfo(account, SyncPhase.Syncing);
            StatusText.Text = $"Updating the copy of {account.DisplayAddress}…";
            var created = !File.Exists(path);
            if (created)
            {
                await Task.Run(() => PstEngineFactory.Create(path, account.DisplayAddress).Dispose());
            }
            if (!_stores.ContainsKey(path)) await OpenArchiveAsync(path, remember: false);
            if (!_stores.TryGetValue(path, out var store) || !store.CanWrite)
            {
                _mirrorStatus[account.AccountId] = "the file cannot be opened for writing";
                StatusText.Text = $"The mailbox copy of {account.DisplayAddress} cannot be opened for writing.";
                return;
            }
            IMailSyncSource source;
            if (account.Provider == OAuthProvider.Google)
                source = new GmailMirrorSource(GetGmailMailbox(account), account.CanModifyGmail);                  // a sign-in that may only read cannot send changes back
            else
            {
                var session = GetMicrosoftSession(account);
                source = new GraphMirrorSource(new GraphMailFolderReader(_graphHttp, account.AccountId), new GraphMailboxSyncReader(_graphHttp, account.AccountId),
                    new GraphInboxReader(_graphHttp, account.AccountId), ct => session.GetAccessTokenAsync(ct),
                    account.CanWriteMicrosoftMail ? new GraphMailWriter(_graphHttp, account.AccountId) : null);
            }
            var progress = new Progress<MirrorProgress>(p =>
            {
                if (p.Phase == "messages" && p.Total > 0) StatusText.Text = $"Updating {account.DisplayAddress}: {p.Folder} ({p.Done + 1} of {p.Total})…";
            });
            var syncOptions = new MirrorSyncOptions(options.KeepMonths, options.MaxAttachmentBytes);
            MirrorSyncResult result;
            using (var state = new SyncStateStore(System.IO.Path.ChangeExtension(path, ".sync")))
                result = await Task.Run(() => MirrorSyncEngine.SyncAsync(source, store, state, syncOptions, progress, CancellationToken.None));
            if (result.Changed || created) RefreshMirrorNode(path, store);
            _mirrorLastSync[account.AccountId] = DateTime.UtcNow;
            if (!result.Offline) AfterMirrorSyncLocal(account, result.Changed || result.Pushed > 0, syncStarted);
            if (result.Offline)
            {
                var waiting = result.PendingLocal > 0 ? $"; {result.PendingLocal} change{(result.PendingLocal == 1 ? "" : "s")} waiting to be sent" : "";
                _mirrorStatus[account.AccountId] = "working offline" + waiting;
                SetMirrorInfo(account, SyncPhase.Offline, waiting: result.PendingLocal);
                StatusText.Text = $"Working offline: the copy of {account.DisplayAddress} stays available{waiting}.";
                return;
            }
            var parts = new List<string>();
            if (result.MessagesAdded > 0) parts.Add($"{result.MessagesAdded} new");
            if (result.MessagesRemoved > 0) parts.Add($"{result.MessagesRemoved} removed");
            if (result.MessagesUpdated > 0) parts.Add($"{result.MessagesUpdated} updated");
            if (result.Pushed > 0) parts.Add($"{result.Pushed} change{(result.Pushed == 1 ? "" : "s")} sent to the server");
            var summary = parts.Count > 0 ? string.Join(", ", parts) : "up to date";
            var notes = new List<string>();
            if (result.Failed > 0) notes.Add($"{result.Failed} problem{(result.Failed == 1 ? "" : "s")} ({result.FirstError})");
            if (result.PendingLocal > 0) notes.Add($"{result.PendingLocal} change{(result.PendingLocal == 1 ? "" : "s")} not sent yet");
            if (result.LocalOnly > 0) notes.Add($"{result.LocalOnly} message{(result.LocalOnly == 1 ? "" : "s")} only in this copy");
            _mirrorStatus[account.AccountId] = notes.Count > 0 ? summary + "; " + string.Join("; ", notes) : summary;
            _mirrorInfo[account.AccountId] = new MirrorInfo(account.DisplayAddress, result.Failed > 0 ? SyncPhase.Problem : SyncPhase.Idle,
                result.Failed > 0 ? $"{result.Failed} problem{(result.Failed == 1 ? "" : "s")} ({result.FirstError})" : (notes.Count > 0 ? string.Join("; ", notes) : ""), DateTime.UtcNow, result.PendingLocal);
            UpdateSyncIndicator();
            StatusText.Text = $"Mailbox copy of {account.DisplayAddress}: {summary}.";
        }
        catch (Exception e) when ((e is GraphMailException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden }) ||
                                  (e is OpenOutlook.Providers.Google.GmailReadException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden }))
        {
            _mirrorStatus[account.AccountId] = "sign in again from Account Settings";
            SetMirrorInfo(account, SyncPhase.Problem, "sign in again from Account Settings");
            StatusText.Text = $"The mailbox copy of {account.DisplayAddress} could not be updated: sign in again from Account Settings.";
        }
        catch (Exception e)
        {
            AppLog.Error("mirror", e, "mailbox copy sync failed");
            _mirrorStatus[account.AccountId] = "could not synchronise: " + e.Message;
            SetMirrorInfo(account, SyncPhase.Problem, "could not synchronise: " + e.Message);
            StatusText.Text = $"The mailbox copy of {account.DisplayAddress} could not be updated: {e.Message}";
        }
        finally
        {
            _mirrorBusy = false;
            _accountSettings?.Refresh();
        }
    }

    /// <summary>The tree node of a mirror archive is rebuilt after a sync so new folders and counts show.</summary>
    private void RefreshMirrorNode(string path, IPstEngine store)
    {
        var old = FolderTree.Items.OfType<TreeViewItem>().FirstOrDefault(i => Equals(i.Tag, path));
        var wasSelected = old is not null && (FolderTree.SelectedItem == old || IsDescendantSelected(old));
        var index = old is null ? FolderTree.Items.Count : FolderTree.Items.IndexOf(old);
        if (old is not null && !wasSelected) FolderTree.Items.Remove(old);
        else if (old is not null) return;                                                      // the user is reading it right now: the tree refreshes on the next selection
        FolderTree.Items.Insert(Math.Min(index, FolderTree.Items.Count), BuildArchiveNode(path, store));
        ApplyFolderOrder(FolderTree);
    }

    private static bool IsDescendantSelected(TreeViewItem node) => node.Items.OfType<TreeViewItem>().Any(c => c.IsSelected || IsDescendantSelected(c));
}
