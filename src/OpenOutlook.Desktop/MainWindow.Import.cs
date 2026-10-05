using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OpenOutlook.Auth;
using OpenOutlook.Providers.Microsoft;
using System.Net.Http;
using PstCore;

namespace OpenOutlook.Desktop;

/// <summary>
/// Getting mail INTO archives: import of .eml files and folders of them (File &gt; Import, or drop files on a folder or the message list),
/// copying messages from a connected mailbox into an archive folder (raw MIME from Graph, parsed and filed natively) and copying or moving
/// messages from one archive to another.
/// </summary>
public partial class MainWindow
{
    private const int ImportBatchFiles = 100;
    private const long ImportBatchBytes = 32L * 1024 * 1024;

    // ------------------------------------------------------------------------------------------------------------ File > Import
    private void ImportClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control anchor) return;
        var menu = new MenuFlyout();
        var files = new MenuItem { Header = "E-mail files (.eml)…" };
        files.Click += async (_, _) => await ImportEmlFilesFromPickerAsync();
        var folder = new MenuItem { Header = "A folder of .eml files (with subfolders)…" };
        folder.Click += async (_, _) => await ImportEmlFolderFromPickerAsync();
        menu.Items.Add(files);
        menu.Items.Add(folder);
        menu.ShowAt(anchor);
    }

    /// <summary>The archive folder imports go to: the folder currently open in an editable archive.</summary>
    private bool TryGetImportTarget(out string archivePath, out MailFolder folder)
    {
        archivePath = _activePath ?? "";
        folder = _activeFolder!;
        if (_activePath is null || _activeFolder is null || !_stores.ContainsKey(_activePath))
        {
            StatusText.Text = "Open a folder of an archive first - imported messages are filed into the selected folder.";
            return false;
        }
        return EnsureWritableStore(_activePath) is not null;
    }

    private async Task ImportEmlFilesFromPickerAsync()
    {
        if (!TryGetImportTarget(out var path, out var folder)) return;
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Import e-mail files into {folder.Name}",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("E-mail messages") { Patterns = ["*.eml"] }, FilePickerFileTypes.All]
        });
        var paths = picked.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList();
        if (paths.Count > 0) await ImportEmlFilesAsync(paths, path, folder);
    }

    private async Task ImportEmlFolderFromPickerAsync()
    {
        if (!TryGetImportTarget(out var path, out var folder)) return;
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = $"Import a folder of e-mail files into {folder.Name}",
            AllowMultiple = false
        });
        if (picked.Count == 1 && picked[0].TryGetLocalPath() is { Length: > 0 } directory)
            await ImportEmlFolderAsync(directory, path, folder);
    }

    // ------------------------------------------------------------------------------------------------------------ the import itself
    private sealed record ImportOutcome(int Imported, int Skipped, string? FirstError);

    /// <summary>Parses and files .eml files in batches (bounded memory), reporting progress in the status bar.</summary>
    private async Task<ImportOutcome> ImportEmlBatchesAsync(IReadOnlyList<string> files, IPstEngine store, MailFolder folder, string label)
    {
        int imported = 0, skipped = 0;
        string? firstError = null;
        var index = 0;
        while (index < files.Count)
        {
            var batchFiles = new List<string>();
            long bytes = 0;
            while (index < files.Count && batchFiles.Count < ImportBatchFiles && (batchFiles.Count == 0 || bytes < ImportBatchBytes))
            {
                try { bytes += new FileInfo(files[index]).Length; } catch (IOException) { }
                batchFiles.Add(files[index++]);
            }
            var done = imported + skipped;
            StatusText.Text = $"{label}: reading {done + 1}–{done + batchFiles.Count} of {files.Count}…";
            var (mails, errors) = await Task.Run(() =>
            {
                var parsed = new List<MailImport>();
                var failed = new List<string>();
                foreach (var file in batchFiles)
                {
                    try { parsed.Add(EmlParser.Parse(File.ReadAllBytes(file), markRead: true)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OutOfMemoryException)
                    { failed.Add($"{Path.GetFileName(file)}: {ex.Message}"); }
                }
                return (parsed, failed);
            });
            skipped += errors.Count;
            firstError ??= errors.FirstOrDefault();
            if (mails.Count == 0) continue;
            StatusText.Text = $"{label}: filing {mails.Count} message{(mails.Count == 1 ? "" : "s")} into {folder.Name}…";
            await Task.Run(() => store.ImportMessages(folder, mails));
            imported += mails.Count;
        }
        return new ImportOutcome(imported, skipped, firstError);
    }

    private async Task ImportEmlFilesAsync(IReadOnlyList<string> files, string archivePath, MailFolder folder)
    {
        var store = EnsureWritableStore(archivePath);
        if (store is null) return;
        try
        {
            var outcome = await ImportEmlBatchesAsync(files, store, folder, "Import");
            AfterImport(archivePath, folder);
            StatusText.Text = ImportSummary(outcome, folder.Name, archivePath);
        }
        catch (Exception ex) when (ex is PstException or IOException)
        {
            AfterImport(archivePath, folder);
            StatusText.Text = $"Import stopped: {ex.Message}";
        }
    }

    private async Task ImportEmlFolderAsync(string directory, string archivePath, MailFolder target)
    {
        var store = EnsureWritableStore(archivePath);
        if (store is null) return;
        int imported = 0, skipped = 0, folders = 0;
        string? firstError = null;
        try
        {
            async Task Walk(string dir, MailFolder into)
            {
                var emls = Directory.EnumerateFiles(dir, "*.eml").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                if (emls.Count > 0)
                {
                    var outcome = await ImportEmlBatchesAsync(emls, store, into, $"Import ({Path.GetFileName(dir)})");
                    imported += outcome.Imported; skipped += outcome.Skipped; firstError ??= outcome.FirstError;
                }
                foreach (var sub in Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    var name = SafeFolderName(Path.GetFileName(sub));
                    if (name.Length == 0) continue;
                    var child = into.Children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (child is null) { child = await Task.Run(() => store.CreateFolder(into.Nid, name)); folders++; }
                    await Walk(sub, child);
                }
            }
            await Walk(directory, target);
        }
        catch (Exception ex) when (ex is PstException or IOException or UnauthorizedAccessException)
        {
            AfterImport(archivePath, target, rebuildTree: folders > 0);
            StatusText.Text = $"Import stopped after {imported} message{(imported == 1 ? "" : "s")}: {ex.Message}";
            return;
        }
        AfterImport(archivePath, target, rebuildTree: folders > 0);
        StatusText.Text = ImportSummary(new ImportOutcome(imported, skipped, firstError), target.Name, archivePath) +
                          (folders > 0 ? $" · {folders} folder{(folders == 1 ? "" : "s")} created" : "");
    }

    private static string SafeFolderName(string name)
    {
        var cleaned = new string(name.Select(c => c is '/' or '\\' or ':' || char.IsControl(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length > 200 ? cleaned[..200].Trim() : cleaned;
    }

    private static string ImportSummary(ImportOutcome o, string folderName, string archivePath) =>
        $"Imported {o.Imported} message{(o.Imported == 1 ? "" : "s")} into {folderName} in {Path.GetFileName(archivePath)} · saved" +
        (o.Skipped > 0 ? $" · {o.Skipped} skipped ({o.FirstError})" : "");

    /// <summary>Refreshes what the user sees after messages or folders were added to an archive.</summary>
    private void AfterImport(string archivePath, MailFolder folder, bool rebuildTree = false)
    {
        InvalidateFolderCache(archivePath);
        if (_activePath == archivePath && _activeFolder?.Nid == folder.Nid) _ = RefreshActivePstFolderAsync(archivePath);
        if (rebuildTree && _stores.TryGetValue(archivePath, out var store))
        {
            var old = FolderTree.Items.OfType<TreeViewItem>().FirstOrDefault(i => Equals(i.Tag, archivePath));
            var index = old is null ? FolderTree.Items.Count : FolderTree.Items.IndexOf(old);
            if (old is not null) FolderTree.Items.Remove(old);
            FolderTree.Items.Insert(Math.Min(index, FolderTree.Items.Count), BuildArchiveNode(archivePath, store));
            ApplyFolderOrder(FolderTree);
        }
    }

    // ------------------------------------------------------------------------------------------------------------ dropping files
    private static List<string> DroppedEmlFiles(IDataObject data)
    {
        if (!data.Contains(DataFormats.Files)) return [];
        return (data.GetFiles() ?? []).Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!).Where(p => p.EndsWith(".eml", StringComparison.OrdinalIgnoreCase) && File.Exists(p)).ToList();
    }

    private static List<string> DroppedFolders(IDataObject data)
    {
        if (!data.Contains(DataFormats.Files)) return [];
        return (data.GetFiles() ?? []).Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!).Where(Directory.Exists).ToList();
    }

    private bool CanAcceptFileDrop(IDataObject data, string archivePath) =>
        _stores.TryGetValue(archivePath, out var store) && store.CanWrite &&
        (DroppedEmlFiles(data).Count > 0 || DroppedFolders(data).Count > 0);

    private async Task HandleFileDropAsync(IDataObject data, string archivePath, MailFolder folder)
    {
        var folders = DroppedFolders(data);
        var files = DroppedEmlFiles(data);
        foreach (var directory in folders) await ImportEmlFolderAsync(directory, archivePath, folder);
        if (files.Count > 0) await ImportEmlFilesAsync(files, archivePath, folder);
    }

    /// <summary>Lets .eml files (and folders of them) be dropped on the message list: they are filed into the folder that is open.</summary>
    private void InitMessageListFileDrop()
    {
        DragDrop.SetAllowDrop(MessageList, true);
        MessageList.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (_activePath is { } p && CanAcceptFileDrop(e.Data, p)) { e.DragEffects = DragDropEffects.Copy; e.Handled = true; }
        });
        MessageList.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            if (_activePath is not { } p || _activeFolder is not { } f || !CanAcceptFileDrop(e.Data, p)) return;
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
            await HandleFileDropAsync(e.Data, p, f);
        });
    }

    // ------------------------------------------------------------------------------------------------------------ copy to an archive folder
    private sealed record ArchiveFolderChoice(string Path, MailFolder Folder, string Label);

    /// <summary>Folders of every editable archive that can receive messages, labelled "Archive / Folder / Subfolder".</summary>
    private List<ArchiveFolderChoice> WritableArchiveFolders()
    {
        var choices = new List<ArchiveFolderChoice>();
        foreach (var (path, store) in _stores.OrderBy(s => s.Value.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            if (!store.CanWrite) continue;
            void Collect(MailFolder f, string prefix)
            {
                var label = prefix + f.Name;
                choices.Add(new ArchiveFolderChoice(path, f, label));
                foreach (var c in f.Children.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)) Collect(c, label + " / ");
            }
            foreach (var root in PstFolderPresentation.VisibleRoots(store.Root))
                Collect(root, store.DisplayName + " / ");
        }
        return choices;
    }

    private async Task<ArchiveFolderChoice?> PickArchiveFolderAsync(string title, string okText)
    {
        var choices = WritableArchiveFolders();
        if (choices.Count == 0)
        {
            StatusText.Text = "No editable archive is open to copy into.";
            return null;
        }
        var dialog = new Window { Title = title, Width = 520, Height = 460, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var list = new ListBox();
        foreach (var c in choices) list.Items.Add(new ListBoxItem { Content = c.Label, Tag = c });
        var ok = new Button { Content = okText, IsEnabled = false, IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        list.SelectionChanged += (_, _) => ok.IsEnabled = list.SelectedItem is not null;
        ok.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = new DockPanel
        {
            Margin = new Thickness(14), LastChildFill = true, Children =
            {
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 10, 0, 0), Children = { ok, cancel } }.WithDock(Dock.Bottom),
                list
            }
        };
        return await dialog.ShowDialog<bool>(this) && list.SelectedItem is ListBoxItem { Tag: ArchiveFolderChoice choice } ? choice : null;
    }

    /// <summary>Context menu "Copy to archive folder...": messages of an archive or of a connected mailbox into any editable archive folder.</summary>
    private async Task CopyToArchiveViaDialogAsync()
    {
        var pstRows = MessageList.SelectedItems.OfType<MessageListRow>().ToList();
        var graphRows = MessageList.SelectedItems.OfType<GraphMessageListRow>().ToList();
        if (pstRows.Count == 0 && graphRows.Count == 0) { StatusText.Text = "Select the messages to copy first."; return; }
        var count = pstRows.Count + graphRows.Count;
        var choice = await PickArchiveFolderAsync($"Copy {(count == 1 ? "message" : $"{count} messages")} to an archive folder", "Copy");
        if (choice is null) return;
        if (pstRows.Count > 0 && _activePath is { } sourcePath && _activeFolder is { } sourceFolder)
            await MoveRowsToFolderAsync(sourcePath, sourceFolder.Nid, pstRows.Select(r => r.Summary.Nid).ToArray(), choice.Folder, copy: true, destPath: choice.Path);
        else if (graphRows.Count > 0 && _activeMicrosoftAccount is { } account)
            await CopyGraphMessagesToArchiveAsync(account, graphRows.Select(r => r.Message).ToList(), choice);
    }

    private async Task CopyGraphMessagesToArchiveAsync(ConnectedAccount account, IReadOnlyList<GraphInboxMessage> messages, ArchiveFolderChoice target)
    {
        var store = EnsureWritableStore(target.Path);
        if (store is null) return;
        int copied = 0, failed = 0;
        string? firstError = null;
        var reader = new GraphInboxReader(_graphHttp, account.AccountId);
        try
        {
            var batch = new List<MailImport>();
            async Task Flush()
            {
                if (batch.Count == 0) return;
                var toWrite = batch.ToList();
                batch.Clear();
                await Task.Run(() => store.ImportMessages(target.Folder, toWrite));
                copied += toWrite.Count;
            }
            long batchBytes = 0;
            for (var i = 0; i < messages.Count; i++)
            {
                StatusText.Text = $"Copying message {i + 1} of {messages.Count} to {target.Label}…";
                try
                {
                    var token = await GetMicrosoftSession(account).GetAccessTokenAsync();
                    var raw = await reader.GetMessageMimeAsync(token, messages[i].Id);
                    var mail = await Task.Run(() => EmlParser.Parse(raw, markRead: messages[i].IsRead));
                    batch.Add(mail);
                    batchBytes += raw.Length;
                    if (batch.Count >= ImportBatchFiles || batchBytes >= ImportBatchBytes) { await Flush(); batchBytes = 0; }
                }
                catch (Exception ex) when (ex is GraphMailException or InvalidDataException or HttpRequestException)
                {
                    failed++;
                    firstError ??= ex.Message;
                }
            }
            await Flush();
        }
        catch (Exception ex) when (ex is PstException or IOException)
        {
            firstError ??= ex.Message;
            failed++;
        }
        AfterImport(target.Path, target.Folder);
        StatusText.Text = $"Copied {copied} message{(copied == 1 ? "" : "s")} to {target.Label} · saved" +
                          (failed > 0 ? $" · {failed} not copied ({firstError})" : "");
    }

    // ------------------------------------------------------------------------------------------------------------ archive to archive
    private async Task CopyBetweenArchivesAsync(string sourcePath, uint sourceFolderNid, uint[] nids, string destPath, MailFolder destFolder, bool copy)
    {
        var source = copy ? (_stores.TryGetValue(sourcePath, out var s0) ? s0 : null) : EnsureWritableStore(sourcePath);
        var dest = EnsureWritableStore(destPath);
        if (source is null || dest is null) return;
        IReadOnlyList<MailSummary> summaries;
        try
        {
            var sourceFolder = source.FindFolder(sourceFolderNid) ?? throw new PstException("The source folder is no longer present.");
            var wanted = nids.ToHashSet();
            summaries = (_folderCache.TryGetValue((sourcePath, sourceFolderNid), out var cached) && cached.Count > 0
                ? cached : await Task.Run(() => source.GetMessages(sourceFolder))).Where(m => wanted.Contains(m.Nid)).ToList();
            StatusText.Text = $"{(copy ? "Copying" : "Moving")} {summaries.Count} message{(summaries.Count == 1 ? "" : "s")} to {destFolder.Name} in {Path.GetFileName(destPath)}…";
            await Task.Run(() => source.CopyMessagesTo(dest, destFolder, summaries));
            if (!copy)
                await Task.Run(() => { foreach (var m in summaries) source.DeleteMessage(m); });
        }
        catch (Exception ex) when (ex is PstException or IOException)
        {
            StatusText.Text = $"Could not {(copy ? "copy" : "move")} between archives: {ex.Message}";
            return;
        }
        InvalidateFolderCache(sourcePath);
        AfterImport(destPath, destFolder);
        if (_activePath == sourcePath && _activeFolder?.Nid == sourceFolderNid) _ = RefreshActivePstFolderAsync(sourcePath);
        StatusText.Text = $"{(copy ? "Copied" : "Moved")} {summaries.Count} message{(summaries.Count == 1 ? "" : "s")} to {destFolder.Name} in {Path.GetFileName(destPath)} · saved";
    }
}

internal static class DockPanelExtensions
{
    public static T WithDock<T>(this T control, Dock dock) where T : Control
    {
        DockPanel.SetDock(control, dock);
        return control;
    }
}
