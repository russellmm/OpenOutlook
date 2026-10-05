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
/// shown in the folder tree like any archive, so reading is from the file. Server to file only for now; changes made in the copy are not sent back yet.
/// </summary>
public partial class MainWindow
{
    private readonly MirrorSettingsStore _mirrorSettings = new();
    private readonly Dictionary<string, string> _mirrorStatus = new(StringComparer.Ordinal);
    private DispatcherTimer? _mirrorTimer;
    private bool _mirrorBusy;
    private DataFilesWindow? _dataFiles;

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
    }

    private IEnumerable<ConnectedAccount> MirrorAccounts()
    {
        try { return _accountRegistry.Load().Where(a => a.Provider == OAuthProvider.MicrosoftConsumers).ToList(); }
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
        if (!options.Enabled) { _mirrorStatus[account.AccountId] = "turned off"; return; }
        _mirrorBusy = true;
        var path = MirrorLocations.PstPathFor(settings, account.AccountId, account.DisplayAddress);
        try
        {
            var check = MirrorLocations.Check(System.IO.Path.GetDirectoryName(path)!);
            if (!check.Ok) { _mirrorStatus[account.AccountId] = "the folder cannot be used: " + check.Problem; StatusText.Text = $"The mailbox copy folder of {account.DisplayAddress} cannot be used ({check.Problem}). Change it in Data Files."; return; }
            _mirrorStatus[account.AccountId] = "synchronising…";
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
            var session = GetMicrosoftSession(account);
            var source = new GraphMirrorSource(new GraphMailFolderReader(_graphHttp, account.AccountId), new GraphMailboxSyncReader(_graphHttp, account.AccountId),
                new GraphInboxReader(_graphHttp, account.AccountId), ct => session.GetAccessTokenAsync(ct));
            var progress = new Progress<MirrorProgress>(p =>
            {
                if (p.Phase == "messages" && p.Total > 0) StatusText.Text = $"Updating {account.DisplayAddress}: {p.Folder} ({p.Done + 1} of {p.Total})…";
            });
            var syncOptions = new MirrorSyncOptions(options.KeepMonths, options.MaxAttachmentBytes);
            MirrorSyncResult result;
            using (var state = new SyncStateStore(System.IO.Path.ChangeExtension(path, ".sync")))
                result = await Task.Run(() => MirrorSyncEngine.SyncAsync(source, store, state, syncOptions, progress, CancellationToken.None));
            if (result.Changed || created) RefreshMirrorNode(path, store);
            var summary = result.Changed
                ? $"{result.MessagesAdded} new, {result.MessagesRemoved} removed, {result.MessagesUpdated} updated"
                : "up to date";
            _mirrorStatus[account.AccountId] = result.Failed > 0 ? $"{summary}; {result.Failed} message(s) could not be copied ({result.FirstError})" : summary;
            StatusText.Text = $"Mailbox copy of {account.DisplayAddress}: {summary}.";
        }
        catch (GraphMailException e) when (e.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            _mirrorStatus[account.AccountId] = "sign in again from Account Settings";
            StatusText.Text = $"The mailbox copy of {account.DisplayAddress} could not be updated: sign in again from Account Settings.";
        }
        catch (Exception e)
        {
            AppLog.Error("mirror", e, "mailbox copy sync failed");
            _mirrorStatus[account.AccountId] = "could not synchronise: " + e.Message;
            StatusText.Text = $"The mailbox copy of {account.DisplayAddress} could not be updated: {e.Message}";
        }
        finally
        {
            _mirrorBusy = false;
            _dataFiles?.Refresh();
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

    // ---- Data Files screen -------------------------------------------------------------------------------------------------------------------------

    private void BsDataFilesInfoClicked(object? sender, RoutedEventArgs e) => ShowDataFiles();

    private void ShowDataFiles()
    {
        if (_dataFiles is { } open) { open.Activate(); return; }
        var window = new DataFilesWindow(BuildDataFileRows, async id => { var a = FindMirrorAccount(id); if (a is not null) await SyncMirrorAsync(a, manual: true); },
            ChangeMirrorLocationAsync, OpenMirrorLocation, (id, s) => SaveMirrorSettings(id, s));
        window.Closed += (_, _) => _dataFiles = null;
        _dataFiles = window;
        window.Show(this);
    }

    private ConnectedAccount? FindMirrorAccount(string accountId) => MirrorAccounts().FirstOrDefault(a => a.AccountId == accountId);

    private IReadOnlyList<DataFileRow> BuildDataFileRows()
    {
        var settings = _mirrorSettings.Load();
        var rows = new List<DataFileRow>();
        IReadOnlyList<ConnectedAccount> all;
        try { all = _accountRegistry.Load(); } catch (Exception) { all = []; }
        foreach (var a in all)
        {
            var supported = a.Provider == OAuthProvider.MicrosoftConsumers;
            var path = MirrorLocations.PstPathFor(settings, a.AccountId, a.DisplayAddress);
            long size = 0;
            DateTime? last = null;
            try
            {
                if (File.Exists(path)) size = new FileInfo(path).Length;
                var syncFile = System.IO.Path.ChangeExtension(path, ".sync");
                if (supported && File.Exists(syncFile) && !_mirrorBusy)
                {
                    using var st = new SyncStateStore(syncFile);
                    if (st.GetMeta("last_sync") is { } s && DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d)) last = d;
                }
            }
            catch (Exception) { /* a file in use: shown as it is */ }
            _mirrorStatus.TryGetValue(a.AccountId, out var status);
            var opts = settings.For(a.AccountId);
            rows.Add(new DataFileRow(a.AccountId, a.DisplayAddress, a.Provider == OAuthProvider.Google ? "Gmail" : "Microsoft", path, size, last,
                status ?? (opts.Enabled ? (File.Exists(path) ? "ready" : "waiting for the first sync") : "turned off"), opts, supported));
        }
        return rows;
    }

    private void SaveMirrorSettings(string accountId, MirrorAccountSettings value)
    {
        var s = _mirrorSettings.Load();
        var accounts = new Dictionary<string, MirrorAccountSettings>(s.Accounts, StringComparer.Ordinal) { [accountId] = value };
        _mirrorSettings.Save(s with { Accounts = accounts });
    }

    private void OpenMirrorLocation(string accountId)
    {
        var account = FindMirrorAccount(accountId);
        if (account is null) return;
        var path = MirrorPathFor(account);
        var folder = System.IO.Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                start.ArgumentList.Add(File.Exists(path) ? "/select," + path : folder);
                Process.Start(start)?.Dispose();
            }
            else
            {
                var start = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false };
                start.ArgumentList.Add(folder);
                Process.Start(start)?.Dispose();
            }
        }
        catch (Exception e) { StatusText.Text = "Could not open the folder: " + e.Message; }
    }

    /// <summary>Choose a new folder: the file is closed, copied there and verified, then the account uses the new location. The old file is left where it was.</summary>
    private async Task<string?> ChangeMirrorLocationAsync(string accountId)
    {
        var account = FindMirrorAccount(accountId);
        if (account is null) return null;
        if (_mirrorBusy) return "A mailbox copy is being updated; try again when it has finished.";
        var folders = await SafePick.FoldersAsync((Window?)_dataFiles ?? this, new FolderPickerOpenOptions { Title = "Choose where to keep the mailbox copy of " + account.DisplayAddress, AllowMultiple = false }, f => StatusText.Text = f);
        var target = folders.FirstOrDefault()?.TryGetLocalPath();
        if (target is null) return null;
        var oldPath = MirrorPathFor(account);
        var newPath = System.IO.Path.Combine(target, System.IO.Path.GetFileName(oldPath));
        if (string.Equals(System.IO.Path.GetFullPath(oldPath), System.IO.Path.GetFullPath(newPath), StringComparison.OrdinalIgnoreCase)) return "That is the current location.";
        long need = File.Exists(oldPath) ? new FileInfo(oldPath).Length : 0;
        var check = MirrorLocations.Check(target, need);
        if (!check.Ok) return check.Problem switch
        {
            MirrorFolderProblem.NotEnoughSpace => "There is not enough free space in that folder.",
            MirrorFolderProblem.NotWritable => "That folder cannot be written to.",
            _ => "That folder cannot be used."
        };
        if (File.Exists(newPath)) return "A file with that name already exists in the chosen folder; nothing was moved.";
        _mirrorBusy = true;
        try
        {
            if (_stores.TryGetValue(oldPath, out var open)) await DetachAsync(oldPath, open);
            if (File.Exists(oldPath))
            {
                await Task.Run(() =>
                {
                    File.Copy(oldPath, newPath, overwrite: false);
                    var oldSync = System.IO.Path.ChangeExtension(oldPath, ".sync");
                    if (File.Exists(oldSync)) File.Copy(oldSync, System.IO.Path.ChangeExtension(newPath, ".sync"), overwrite: false);
                });
                using var verify = PstEngineFactory.Open(newPath);
                var findings = verify.Scan().Findings;
                if (findings.Count > 0) { File.Delete(newPath); return "The copy did not check out clean, so it was not used; the mailbox copy stays where it was."; }
            }
            var s = _mirrorSettings.Load();
            var accounts = new Dictionary<string, MirrorAccountSettings>(s.Accounts, StringComparer.Ordinal) { [accountId] = s.For(accountId) with { FolderOverride = target } };
            _mirrorSettings.Save(s with { Accounts = accounts });
        }
        catch (Exception e)
        {
            AppLog.Error("mirror", e, "moving the mailbox copy failed");
            return "The mailbox copy could not be moved: " + e.Message;
        }
        finally { _mirrorBusy = false; }
        await OpenArchiveAsync(newPath, remember: false);
        return $"The mailbox copy is now in {target}. The old file in {System.IO.Path.GetDirectoryName(oldPath)} was left in place; delete it when you no longer need it.";
    }
}
