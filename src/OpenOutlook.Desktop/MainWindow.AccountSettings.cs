using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OpenOutlook.Auth;
using OpenOutlook.Mirror;
using OpenOutlook.PstNative;

namespace OpenOutlook.Desktop;

/// <summary>Account Settings (Outlook's dialog): the Email tab lists the connected accounts, the Data Files tab the local mailbox copies and the opened PST files.</summary>
public partial class MainWindow
{
    private AccountSettingsWindow? _accountSettings;
    private readonly AccountDefaultStore _accountDefault = new();

    private void ShowAccountSettings(bool dataFiles = false)
    {
        if (_accountSettings is { } open) { open.Activate(); return; }
        var window = new AccountSettingsWindow(new AccountSettingsHost(this), dataFiles);
        window.Closed += (_, _) => _accountSettings = null;
        _accountSettings = window;
        window.Show(this);
    }

    private void BsDataFilesInfoClicked(object? sender, RoutedEventArgs e) => ShowAccountSettings(dataFiles: true);

    /// <summary>The File > Info "Account Settings" menu, as in Outlook; items that are not built yet say so.</summary>
    private void ShowAccountSettingsMenu(Control anchor)
    {
        MenuItem Item(string title, string description, Action? run, bool built = true)
        {
            var text = new StackPanel
            {
                Margin = new Avalonia.Thickness(4, 6), MaxWidth = 380,
                Children =
                {
                    new TextBlock { Text = title, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                    new TextBlock { Text = description, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Foreground = Avalonia.Media.Brushes.Gray }
                }
            };
            var item = new MenuItem { Header = text };
            if (!built) ToolTip.SetTip(item, "To be implemented");
            item.Click += (_, _) => { if (run is not null) run(); else StatusText.Text = title.TrimEnd('.', '…') + " is to be implemented."; };
            return item;
        }
        var flyout = new MenuFlyout
        {
            Items =
            {
                Item("Account Settings…", "Add and remove accounts or change existing connection settings.", () => ShowAccountSettings()),
                Item("Account Name and Sync Settings", "Update basic account settings such as account name and folder sync settings.", () => ShowAccountSettings(dataFiles: true)),
                Item("Delegate Access", "Give others permission to receive items and respond on your behalf.", null, built: false),
                Item("Download Address Book…", "Download a copy of the Global Address Book.", null, built: false),
                Item("Change Profile", "Restart OpenOutlook and choose a different profile.", null, built: false),
                Item("Manage Profiles", "Add and remove profiles or change existing profile settings.", null, built: false)
            }
        };
        flyout.ShowAt(anchor);
    }

    private ConnectedAccount? FindMirrorAccount(string accountId) => MirrorAccounts().FirstOrDefault(a => a.AccountId == accountId);

    private IReadOnlyList<DataFileRow> BuildDataFileRows()
    {
        var settings = _mirrorSettings.Load();
        var rows = new List<DataFileRow>();
        IReadOnlyList<ConnectedAccount> all;
        try { all = _accountRegistry.Load(); } catch (Exception) { all = []; }
        var mirrorPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in all)
        {
            var supported = a.Provider == OAuthProvider.MicrosoftConsumers;
            var path = MirrorLocations.PstPathFor(settings, a.AccountId, a.DisplayAddress);
            long size = 0;
            DateTime? last = null;
            try
            {
                if (supported && File.Exists(path)) size = new FileInfo(path).Length;
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
            if (supported) mirrorPaths.Add(System.IO.Path.GetFullPath(path));
            rows.Add(new DataFileRow(a.DisplayAddress, path, "Mailbox copy", size,
                supported ? status ?? (opts.Enabled ? (File.Exists(path) ? "ready" : "waiting for the first sync") : "turned off") : "",
                a.AccountId, last, opts, IsMirror: true, Available: supported));
        }
        foreach (var (path, store) in _stores.ToArray())
        {
            if (mirrorPaths.Contains(System.IO.Path.GetFullPath(path))) continue;
            long size = 0;
            try { size = new FileInfo(path).Length; } catch (Exception) { /* the size stays unknown */ }
            rows.Add(new DataFileRow(store.DisplayName, path, "Outlook data file", size,
                store.CanWrite ? "editable" : "read-only: " + (_readOnlyReasons.TryGetValue(path, out var why) ? why : "not editable")));
        }
        return rows;
    }

    private void SaveMirrorSettings(string accountId, MirrorAccountSettings value)
    {
        var s = _mirrorSettings.Load();
        var accounts = new Dictionary<string, MirrorAccountSettings>(s.Accounts, StringComparer.Ordinal) { [accountId] = value };
        _mirrorSettings.Save(s with { Accounts = accounts });
    }

    private void OpenFileLocationOf(string path)
    {
        var folder = System.IO.Path.GetDirectoryName(path)!;
        try
        {
            Directory.CreateDirectory(folder);
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
    private async Task<string?> ChangeMirrorLocationAsync(string accountId, Window owner)
    {
        var account = FindMirrorAccount(accountId);
        if (account is null) return null;
        if (_mirrorBusy) return "A mailbox copy is being updated; try again when it has finished.";
        var folders = await SafePick.FoldersAsync(owner, new FolderPickerOpenOptions { Title = "Choose where to keep the mailbox copy of " + account.DisplayAddress, AllowMultiple = false }, f => StatusText.Text = f);
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

    private async Task<string?> RemoveAccountCoreAsync(ConnectedAccount account, Window owner)
    {
        var yes = new Button { Content = "Remove account" };
        var no = new Button { Content = "Cancel" };
        var dialog = new Window
        {
            Title = "Remove account", Width = 480, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(18), Spacing = 12,
                Children =
                {
                    new TextBlock { Text = $"Remove {account.DisplayAddress} from OpenOutlook?", FontWeight = Avalonia.Media.FontWeight.SemiBold, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new TextBlock { Text = "The sign-in is deleted from this computer. Nothing is deleted from the mailbox, and the local copy file is kept.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { yes, no } }
                }
            }
        };
        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);
        if (!await dialog.ShowDialog<bool>(owner)) return null;
        try
        {
            await _secrets.DeleteRefreshTokenAsync(account.Provider, account.AccountId);
            _accountRegistry.Remove(account.Provider, account.AccountId);
            var path = MirrorPathFor(account);
            if (_stores.TryGetValue(path, out var store)) await DetachAsync(path, store);
            if (_accountDefault.Load() == account.AccountId) _accountDefault.Save(null);
            RefreshConnectedAccounts();
            return $"{account.DisplayAddress} was removed.";
        }
        catch (Exception e) { return "Could not remove the account: " + e.Message; }
    }

    private sealed class AccountSettingsHost(MainWindow w) : IAccountSettingsHost
    {
        public IReadOnlyList<ConnectedAccount> Accounts() { try { return w._accountRegistry.Load(); } catch (Exception) { return []; } }
        public string? DefaultAccountId => w._accountDefault.Load();
        public void SetDefaultAccount(string accountId) => w._accountDefault.Save(accountId);
        public Task AddAccountAsync() => w.ShowAccountSetupAsync();
        public async Task RepairAccountAsync(ConnectedAccount account)
        {
            await new AccountSetupWindow(account).ShowDialog(w);
            w.RefreshConnectedAccounts();
        }
        public Task<string?> RemoveAccountAsync(ConnectedAccount account, Window owner) => w.RemoveAccountCoreAsync(account, owner);
        public IReadOnlyList<DataFileRow> DataFiles() => w.BuildDataFileRows();
        public async Task<string?> AddDataFileAsync(Window owner)
        {
            var chosen = await SafePick.FilesAsync(owner, new FilePickerOpenOptions
            { Title = "Open an Outlook data file", AllowMultiple = true, FileTypeFilter = [new FilePickerFileType("Outlook PST") { Patterns = ["*.pst"] }] }, f => w.StatusText.Text = f);
            var added = 0;
            foreach (var file in chosen)
                if (file.TryGetLocalPath() is { } p && await w.OpenArchiveAsync(System.IO.Path.GetFullPath(p))) added++;
            return added == 0 ? null : $"{added} data file{(added == 1 ? "" : "s")} added to the folder list.";
        }
        public async Task<string?> RemoveDataFileAsync(DataFileRow row)
        {
            if (row.IsMirror)
            {
                if (row.AccountId is null || !row.Available) return "Nothing to remove.";
                if (w._stores.TryGetValue(row.Path, out var s)) await w.DetachAsync(row.Path, s);
                w.SaveMirrorSettings(row.AccountId, (row.Settings ?? new MirrorAccountSettings()) with { Enabled = false });
                return "The mailbox copy was closed and turned off. The file was not deleted; turn it on again in Settings.";
            }
            if (w._stores.TryGetValue(row.Path, out var store)) { await w.DetachAsync(row.Path, store); return $"{row.Name} was closed; the file was not deleted."; }
            return "That file is no longer open.";
        }
        public void OpenFileLocation(DataFileRow row) => w.OpenFileLocationOf(row.Path);
        public async Task SyncNowAsync(string accountId) { if (w.FindMirrorAccount(accountId) is { } a) await w.SyncMirrorAsync(a, manual: true); }
        public Task<string?> ChangeLocationAsync(string accountId, Window owner) => w.ChangeMirrorLocationAsync(accountId, owner);
        public void SaveMirrorSettings(string accountId, MirrorAccountSettings settings) => w.SaveMirrorSettings(accountId, settings);
    }
}
