using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using OpenOutlook.Auth;
using OpenOutlook.Providers.Google;
using OpenOutlook.Providers.Microsoft;
using PstCore;

namespace OpenOutlook.Desktop;

/// <summary>
/// Copy and move between any two stores: PST files, Microsoft mailboxes and Gmail mailboxes. One "Move Items" / "Copy Items" picker lists every place a message can go.
/// Two PST folders use the engine's own copy (every property, named properties translated, one atomic journaled transaction per batch); the same mailbox uses the
/// server's move/copy; everything else goes through the whole message as MIME (<see cref="MailTransfer"/>), and a move removes an original only after its copy is committed.
/// </summary>
public partial class MainWindow
{
    private bool _transferBusy;

    // ---- where messages come from / go to ------------------------------------------------------------------------------------------------

    private abstract record TransferOrigin;
    private sealed record PstOrigin(string Path, MailFolder Folder, IReadOnlyList<MailSummary> Messages) : TransferOrigin;
    private sealed record MicrosoftOrigin(MicrosoftFolderSelection Folder, IReadOnlyList<GraphInboxMessage> Messages) : TransferOrigin;
    private sealed record GmailOrigin(GmailFolderSelection Folder, IReadOnlyList<GraphInboxMessage> Messages) : TransferOrigin;

    private abstract record TransferTarget;
    private sealed record PstTarget(string Path, MailFolder Folder) : TransferTarget;
    private sealed record MicrosoftTarget(ConnectedAccount Account, MicrosoftFolderSelection Folder) : TransferTarget;
    private sealed record GmailTarget(ConnectedAccount Account, GmailFolderSelection Folder) : TransferTarget;

    /// <summary>The unselectable top rows of the picker (one per store); they carry what is needed to create a folder at the top of that store.</summary>
    private abstract record PickRoot;
    private sealed record PstPickRoot(string Path) : PickRoot;
    private sealed record MicrosoftPickRoot(ConnectedAccount Account) : PickRoot;
    private sealed record GmailPickRoot(ConnectedAccount Account) : PickRoot;

    /// <summary>The messages the user has selected in the folder that is showing.</summary>
    private TransferOrigin? CurrentTransferOrigin()
    {
        if (_activeGmailFolder is { } gmail)
        {
            var rows = MessageList.SelectedItems.OfType<GraphMessageListRow>().Select(r => r.Message).DistinctBy(m => m.Id).ToList();
            return rows.Count == 0 ? null : new GmailOrigin(gmail, rows);
        }
        if (_activeMicrosoftFolder is { } microsoft)
        {
            var rows = MessageList.SelectedItems.OfType<GraphMessageListRow>().Select(r => r.Message).DistinctBy(m => m.Id).ToList();
            return rows.Count == 0 ? null : new MicrosoftOrigin(microsoft, rows);
        }
        if (_activePath is { } path && _activeFolder is { } folder)
        {
            var rows = MessageList.SelectedItems.OfType<MessageListRow>().Select(r => r.Summary).DistinctBy(s => s.Nid).ToList();
            return rows.Count == 0 ? null : new PstOrigin(path, folder, rows);
        }
        return null;
    }

    private static int OriginCount(TransferOrigin o) => o switch { PstOrigin p => p.Messages.Count, MicrosoftOrigin m => m.Messages.Count, GmailOrigin g => g.Messages.Count, _ => 0 };

    // ---- the picker ---------------------------------------------------------------------------------------------------------------------

    /// <summary>One tree for the picker: every editable data file, every Microsoft account and every Gmail account, each with its folders; the folder the messages are in cannot be chosen.</summary>
    private List<FolderPickItem> BuildTransferRoots(TransferOrigin origin)
    {
        var roots = new List<FolderPickItem>();
        var stores = _stores.Where(s => s.Value.CanWrite && !IsMirrorCopy(s.Key)).OrderBy(s => s.Value.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        foreach (var (path, store) in stores)
        {
            FolderPickItem Pick(MailFolder f)
            {
                var here = origin is PstOrigin po && po.Path == path && po.Folder.Nid == f.Nid;
                var item = new FolderPickItem(f.Name, new PstTarget(path, f), f.UnreadCount, selectable: !here);
                foreach (var child in f.Children) item.Children.Add(Pick(child));
                return item;
            }
            var ambiguous = stores.Count(s => string.Equals(s.Value.DisplayName, store.DisplayName, StringComparison.CurrentCultureIgnoreCase)) > 1;
            var root = new FolderPickItem(ambiguous ? $"{store.DisplayName} ({Path.GetFileName(path)})" : store.DisplayName, new PstPickRoot(path), selectable: false);
            foreach (var top in PstFolderPresentation.VisibleRoots(store.Root)) root.Children.Add(Pick(top));
            roots.Add(root);
        }
        foreach (var account in _accountRegistry.Load().OrderBy(a => a.DisplayAddress, StringComparer.CurrentCultureIgnoreCase))
        {
            var node = FolderTree.Items.OfType<TreeViewItem>().FirstOrDefault(i => i.Tag is ConnectedAccount a && a.AccountId == account.AccountId);
            if (node is null) continue;
            if (account.Provider == OAuthProvider.MicrosoftConsumers && account.CanWriteMicrosoftMail)
            {
                FolderPickItem Pick(TreeViewItem n)
                {
                    var sel = (MicrosoftFolderSelection)n.Tag!;
                    var here = origin is MicrosoftOrigin mo && mo.Folder.Account.AccountId == account.AccountId && mo.Folder.Id == sel.Id;
                    var item = new FolderPickItem(sel.Name, new MicrosoftTarget(account, sel), _msUnread.TryGetValue(account.AccountId + "|" + sel.Id, out var n2) ? n2 : 0, selectable: !here);
                    foreach (var child in n.Items.OfType<TreeViewItem>().Where(c => c.Tag is MicrosoftFolderSelection)) item.Children.Add(Pick(child));
                    return item;
                }
                var root = new FolderPickItem(account.DisplayAddress, new MicrosoftPickRoot(account), selectable: false);
                foreach (var child in node.Items.OfType<TreeViewItem>().Where(c => c.Tag is MicrosoftFolderSelection)) root.Children.Add(Pick(child));
                roots.Add(root);
            }
            else if (account.Provider == OAuthProvider.Google && account.CanModifyGmail)
            {
                // Sent, Drafts, Starred and Important are not places to put mail; user labels nest by "Parent/Child" like Gmail shows them.
                var hidden = new HashSet<string> { "SENT", "DRAFT", "STARRED", "IMPORTANT" };
                int Unread(GmailFolderSelection g) => _gmailUnread.TryGetValue(account.AccountId + "|" + g.LabelId, out var n) ? n : 0;
                bool Here(GmailFolderSelection g) => origin is GmailOrigin go && go.Folder.Account.AccountId == account.AccountId && go.Folder.LabelId == g.LabelId;
                var all = node.Items.OfType<TreeViewItem>().Select(i => i.Tag).OfType<GmailFolderSelection>().ToList();
                var root = new FolderPickItem(account.DisplayAddress, new GmailPickRoot(account), selectable: false);
                foreach (var system in all.Where(g => GmailSystemLabels.Any(l => l.Id == g.LabelId) && !hidden.Contains(g.LabelId)))
                    root.Children.Add(new FolderPickItem(system.Name, new GmailTarget(account, system), Unread(system), selectable: !Here(system)));
                var byPath = new Dictionary<string, FolderPickItem>(StringComparer.Ordinal);
                foreach (var user in all.Where(g => GmailSystemLabels.All(l => l.Id != g.LabelId)).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var slash = user.Name.LastIndexOf('/');
                    var item = new FolderPickItem(slash >= 0 ? user.Name[(slash + 1)..] : user.Name, new GmailTarget(account, user), Unread(user), selectable: !Here(user));
                    byPath[user.Name] = item;
                    (slash >= 0 && byPath.TryGetValue(user.Name[..slash], out var parent) ? parent : root).Children.Add(item);
                }
                roots.Add(root);
            }
        }
        return roots;
    }

    /// <summary>Move / Copy to Folder... from the message menu and the ribbon: pick any folder of any store, then transfer.</summary>
    private async Task TransferViaDialogAsync(bool copy)
    {
        if (_transferBusy) { StatusText.Text = "A copy or move is still running."; return; }
        var origin = CurrentTransferOrigin();
        if (origin is null) { StatusText.Text = "Select the messages to " + (copy ? "copy" : "move") + " first."; return; }
        if (!await CheckOriginAllowsAsync(origin, copy)) return;
        var roots = BuildTransferRoots(origin);
        if (!roots.Any(r => r.Children.Any(c => HasSelectable(c)))) { StatusText.Text = "There is no other folder to " + (copy ? "copy" : "move") + " into."; return; }
        var count = OriginCount(origin);
        var noun = count == 1 ? "item" : "items";
        var dialog = new FolderPickerWindow(copy ? "Copy Items" : "Move Items", $"{(copy ? "Copy" : "Move")} the selected {noun} to:", roots, CreateFolderForPickerAsync);
        await dialog.ShowDialog(this);
        TransferTarget? target = dialog.Result?.Tag switch { PstTarget p => p, MicrosoftTarget m => m, GmailTarget g => g, _ => null };
        if (target is null) return;
        await ExecuteTransferAsync(origin, target, copy);
    }

    private static bool HasSelectable(FolderPickItem item) => item.Selectable || item.Children.Any(HasSelectable);

    /// <summary>Moving needs write access to where the messages are now; copying only needs to read them.</summary>
    private async Task<bool> CheckOriginAllowsAsync(TransferOrigin origin, bool copy)
    {
        switch (origin)
        {
            case PstOrigin p when !copy:
                return EnsureWritableStore(p.Path) is not null;
            case MicrosoftOrigin m when !copy && !m.Folder.Account.CanWriteMicrosoftMail:
                await ExplainMailActionAsync("This saved Microsoft sign-in has read-only mail access. Sign in again to allow organizing mail.", m.Folder.Account);
                return false;
            case GmailOrigin g when !copy && !g.Folder.Account.CanModifyGmail:
                await ExplainMailActionAsync("This Gmail sign-in is read-only. Sign in again from Account setup to allow organizing mail.", g.Folder.Account);
                return false;
        }
        return true;
    }

    /// <summary>"New..." in the picker: creates a folder (PST), folder (Microsoft) or label (Gmail) below the selected one and returns it.</summary>
    private async Task<FolderPickItem?> CreateFolderForPickerAsync(FolderPickItem? parent)
    {
        switch (parent?.Tag)
        {
            case PstPickRoot or PstTarget:
            {
                var path = parent.Tag is PstTarget pt ? pt.Path : ((PstPickRoot)parent.Tag).Path;
                var store = EnsureWritableStore(path);
                if (store is null) return null;
                var parentFolder = parent.Tag is PstTarget pf ? pf.Folder : store.Root.Children.FirstOrDefault(c =>
                    c.Name.Equals("Top of Outlook data file", StringComparison.OrdinalIgnoreCase)) ?? store.Root;
                var name = await PromptForFolderNameAsync(parentFolder.Name);
                if (string.IsNullOrWhiteSpace(name)) return null;
                try
                {
                    var created = await Task.Run(() => store.CreateFolder(parentFolder.Nid, name));
                    InvalidateFolderCache(path);
                    var parentNode = parent.Tag is PstTarget p2 ? FindFolderNode(FolderTree.Items.OfType<object>(), p2.Folder.Nid)
                        : FolderTree.Items.OfType<TreeViewItem>().FirstOrDefault(n => n.Tag is string s && s == path);
                    if (parentNode is not null) { AddChildren(parentNode, created, path, new HashSet<uint>()); ApplyFolderOrder(parentNode); parentNode.IsExpanded = true; }
                    StatusText.Text = $"Folder \"{created.Name}\" created.";
                    return new FolderPickItem(created.Name, new PstTarget(path, created));
                }
                catch (Exception ex) when (ex is PstException or IOException)
                {
                    StatusText.Text = $"Could not create folder: {ex.Message}";
                    return null;
                }
            }
            case MicrosoftPickRoot or MicrosoftTarget:
            {
                var account = parent.Tag is MicrosoftTarget mt ? mt.Account : ((MicrosoftPickRoot)parent.Tag).Account;
                var parentSel = (parent.Tag as MicrosoftTarget)?.Folder;
                var rootNode = FolderTree.Items.OfType<TreeViewItem>().FirstOrDefault(i => i.Tag is ConnectedAccount a && a.AccountId == account.AccountId);
                if (rootNode is null) return null;
                var name = await PromptForFolderNameAsync(parentSel?.Name ?? account.DisplayAddress);
                if (string.IsNullOrWhiteSpace(name)) return null;
                try
                {
                    var writer = new GraphMailWriter(_graphHttp, account.AccountId);
                    var created = await writer.CreateFolderAsync(await GetMicrosoftSession(account).GetAccessTokenAsync(), parentSel?.Id, name.Trim());
                    var newSel = new MicrosoftFolderSelection(account, created.Id, created.Name);
                    var node = new TreeViewItem { Header = created.Name, Tag = newSel };
                    EnableFolderReordering(node);
                    var parentNode = parentSel is null ? rootNode : FindMicrosoftNode(rootNode, parentSel.Id) ?? rootNode;
                    parentNode.Items.Add(node);
                    parentNode.IsExpanded = true;
                    StatusText.Text = $"Folder \"{created.Name}\" created.";
                    return new FolderPickItem(created.Name, new MicrosoftTarget(account, newSel));
                }
                catch (GraphMailException error) { StatusText.Text = error.Message; return null; }
                catch (ArgumentException) { StatusText.Text = "That is not a valid folder name."; return null; }
                catch (Exception) { StatusText.Text = "Could not create the folder. Check the connection and retry."; return null; }
            }
            case GmailPickRoot or GmailTarget:
            {
                var account = parent.Tag is GmailTarget gt ? gt.Account : ((GmailPickRoot)parent.Tag).Account;
                var parentLabel = (parent.Tag as GmailTarget)?.Folder is { } p && GmailSystemLabels.All(l => l.Id != p.LabelId) ? p : null;
                var name = await PromptForFolderNameAsync(parentLabel?.Name ?? account.DisplayAddress);
                if (string.IsNullOrWhiteSpace(name)) return null;
                if (name.Contains('/')) { StatusText.Text = "A folder name cannot contain a slash."; return null; }
                try
                {
                    var created = await GetGmailMailbox(account).CreateLabelAsync(parentLabel is null ? name.Trim() : parentLabel.Name + "/" + name.Trim());
                    await RefreshGmailCountsAsync(account);
                    StatusText.Text = $"Folder \"{created.Name}\" created.";
                    return new FolderPickItem(name.Trim(), new GmailTarget(account, new GmailFolderSelection(account, created.Id, created.Name)));
                }
                catch (GmailReadException error) { StatusText.Text = error.Message; return null; }
                catch (ArgumentException) { StatusText.Text = "That is not a valid folder name."; return null; }
                catch (Exception) { StatusText.Text = "Could not create the folder. Check the connection and retry."; return null; }
            }
        }
        return null;
    }

    // ---- doing it -----------------------------------------------------------------------------------------------------------------------

    /// <summary>Runs one copy or move and refreshes whatever is showing. Never throws: every outcome ends in the status bar.</summary>
    private async Task ExecuteTransferAsync(TransferOrigin origin, TransferTarget target, bool copy)
    {
        if (_transferBusy) { StatusText.Text = "A copy or move is still running."; return; }
        _transferBusy = true;
        try
        {
            // Same store: the store's own machinery (PST engine; the mailbox's server-side move / copy).
            switch (origin, target)
            {
                case (PstOrigin po, PstTarget pt):
                    await MoveRowsToFolderAsync(po.Path, po.Folder.Nid, po.Messages.Select(m => m.Nid).ToArray(), pt.Folder, copy, destPath: pt.Path);
                    return;
                case (MicrosoftOrigin mo, MicrosoftTarget mt) when mo.Folder.Account.AccountId == mt.Account.AccountId:
                    _transferBusy = false;
                    await MoveOnlineAsync(mo.Messages.Select(m => m.Id).ToArray(), mt.Folder, copy);
                    return;
                case (GmailOrigin go, GmailTarget gt) when go.Folder.Account.AccountId == gt.Account.AccountId:
                    _transferBusy = false;
                    await MoveOnlineAsync(go.Messages.Select(m => m.Id).ToArray(), gt.Folder, copy);
                    return;
            }
            await TransferThroughMimeAsync(origin, target, copy);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Error("transfer", ex, "copy/move between stores failed");
            StatusText.Text = $"Could not {(copy ? "copy" : "move")} the messages: {ex.Message}";
        }
        finally { _transferBusy = false; }
    }

    private async Task TransferThroughMimeAsync(TransferOrigin origin, TransferTarget target, bool copy)
    {
        var (source, items) = MakeTransferSource(origin);
        var sink = MakeTransferSink(target);
        var progress = new Progress<string>(text => StatusText.Text = text);
        var result = await MailTransfer.RunAsync(source, items, sink, move: !copy, progress);

        // what is on screen
        var where = DescribeTarget(target);
        await RefreshAfterTransferAsync(origin, target, result, copy);
        var verb = copy ? "Copied" : "Moved";
        var text = result.Transferred == 0
            ? $"Could not {(copy ? "copy" : "move")}: {result.Failures.FirstOrDefault()?.Error ?? "nothing was transferred"}"
            : $"{verb} {result.Transferred} message{(result.Transferred == 1 ? "" : "s")} to {where}";
        if (result.Transferred > 0 && target is PstTarget) text += " · saved";
        if (result.Failures.Count > 0 && result.Transferred > 0)
            text += $" · {result.Failures.Count} not {(copy ? "copied" : "moved")}: {result.Failures[0].Error}";
        if (result.RemoveError is not null)
            text += $" · the originals could not be removed ({result.RemoveError}); the copies are in {where}";
        StatusText.Text = text;
    }

    private static string DescribeTarget(TransferTarget t) => t switch
    {
        PstTarget p => $"{p.Folder.Name} in {Path.GetFileName(p.Path)}",
        MicrosoftTarget m => $"{m.Folder.Name} ({m.Account.DisplayAddress})",
        GmailTarget g => $"{g.Folder.Name} ({g.Account.DisplayAddress})",
        _ => "the destination"
    };

    private async Task RefreshAfterTransferAsync(TransferOrigin origin, TransferTarget target, TransferResult result, bool copy)
    {
        var removed = !copy && result.Removed > 0;
        // destination
        switch (target)
        {
            case PstTarget p when result.Transferred > 0:
                AfterImport(p.Path, p.Folder);
                break;
            case MicrosoftTarget m when result.Transferred > 0:
                _ = RefreshMicrosoftFolderCountsAsync(m.Account, CancellationToken.None, force: true);
                await SyncMirrorWhenIdleAsync(m.Account, manual: true);                  // folders open from the mailbox copy: it has to learn about the new messages
                if (_activeMicrosoftFolder is { } am && am.Account.AccountId == m.Account.AccountId && am.Id == m.Folder.Id) await RefreshMicrosoftFolderAsync();
                break;
            case GmailTarget g when result.Transferred > 0:
                _ = RefreshGmailCountsAsync(g.Account);
                if (_activeGmailFolder is { } ag && ag.Account.AccountId == g.Account.AccountId && ag.LabelId == g.Folder.LabelId) await ReloadGmailFolderAsync(ag);
                break;
        }
        // origin (a move took the messages out of the folder that is showing)
        if (!removed) return;
        switch (origin)
        {
            case PstOrigin po:
                InvalidateFolderCache(po.Path);
                if (_activePath == po.Path && _activeFolder?.Nid == po.Folder.Nid) _ = RefreshActivePstFolderAsync(po.Path);
                break;
            case MicrosoftOrigin mo:
                if (_activeMicrosoftFolder == mo.Folder && _currentGraphMessages is { } shown)
                {
                    var gone = mo.Messages.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
                    _currentGraphMessages = shown.Where(x => !gone.Contains(x.Id)).ToList();      // the list drops them at once; the copy catches up below
                    ReconcileGraphMessages(_currentGraphMessages);
                    RefreshItemCount();
                }
                _ = RefreshMicrosoftFolderCountsAsync(mo.Folder.Account, CancellationToken.None, force: true);
                await SyncMirrorWhenIdleAsync(mo.Folder.Account, manual: true);
                if (_activeMicrosoftFolder == mo.Folder) await RefreshMicrosoftFolderAsync();
                break;
            case GmailOrigin go:
                _ = RefreshGmailCountsAsync(go.Folder.Account);
                if (_activeGmailFolder == go.Folder) await ReloadGmailFolderAsync(go.Folder);
                break;
        }
    }

    // ---- drag and drop across stores ------------------------------------------------------------------------------------------------------

    private static TransferTarget? TargetFromNodeTag(object? tag) => tag switch
    {
        FolderSelection f => new PstTarget(f.Path, f.Folder),
        MicrosoftFolderSelection m => new MicrosoftTarget(m.Account, m),
        GmailFolderSelection g => new GmailTarget(g.Account, g),
        _ => null
    };

    private bool CanReceiveTransfer(TransferTarget target) => target switch
    {
        PstTarget p => _stores.TryGetValue(p.Path, out var s) && s.CanWrite && !IsMirrorCopy(p.Path),
        MicrosoftTarget m => m.Account.CanWriteMicrosoftMail,
        GmailTarget g => g.Account.CanModifyGmail && g.Folder.LabelId is not ("SENT" or "DRAFT" or "STARRED" or "IMPORTANT"),
        _ => false
    };

    /// <summary>What a drag payload is, without reading any message: a store key ("pst|path", "microsoft|account", "gmail|account") or null when it is not ours or its folder is not the one showing.</summary>
    private string? DragSourceStore(IDataObject data)
    {
        if (!data.Contains(DataFormats.Text) || data.GetText() is not { } text) return null;
        if (ParseOnlineDragPayload(text) is { } online)
        {
            if (_activeGmailFolder is { } g && g.Account.AccountId == online.AccountId && g.LabelId == online.SourceFolderId) return "gmail|" + online.AccountId;
            if (_activeMicrosoftFolder is { } m && m.Account.AccountId == online.AccountId && m.Id == online.SourceFolderId) return "microsoft|" + online.AccountId;
            return null;
        }
        if (ParseMovePayload(data) is { } pst && _stores.ContainsKey(pst.Path)) return "pst|" + pst.Path;
        return null;
    }

    private static string StoreKeyOf(TransferTarget t) => t switch
    {
        PstTarget p => "pst|" + p.Path,
        MicrosoftTarget m => "microsoft|" + m.Account.AccountId,
        GmailTarget g => "gmail|" + g.Account.AccountId,
        _ => ""
    };

    /// <summary>
    /// True when dropping the dragged messages on this folder needs the cross-store transfer. Two PST folders, and two folders of one mailbox, are handled by the
    /// older handlers (the engine's own copy, the server's move), so they are not claimed here.
    /// </summary>
    private bool IsCrossStoreDrop(IDataObject data, object? nodeTag)
    {
        var target = TargetFromNodeTag(nodeTag);
        if (target is null || !CanReceiveTransfer(target)) return false;
        var source = DragSourceStore(data);
        if (source is null) return false;
        var kind = StoreKeyOf(target);
        if (source == kind) return false;                                                       // same store
        return !(source.StartsWith("pst|", StringComparison.Ordinal) && kind.StartsWith("pst|", StringComparison.Ordinal));   // PST to PST: the engine's copy
    }

    /// <summary>The messages of a drag payload, read from the folder that is showing.</summary>
    private TransferOrigin? OriginFromDrag(IDataObject data)
    {
        if (!data.Contains(DataFormats.Text) || data.GetText() is not { } text) return null;
        if (ParseOnlineDragPayload(text) is { } online)
        {
            var wanted = online.MessageIds.ToHashSet(StringComparer.Ordinal);
            var messages = (_currentGraphMessages ?? []).Where(m => wanted.Contains(m.Id)).ToList();
            if (messages.Count == 0) return null;
            if (_activeGmailFolder is { } g && g.Account.AccountId == online.AccountId && g.LabelId == online.SourceFolderId) return new GmailOrigin(g, messages);
            if (_activeMicrosoftFolder is { } m && m.Account.AccountId == online.AccountId && m.Id == online.SourceFolderId) return new MicrosoftOrigin(m, messages);
            return null;
        }
        if (ParseMovePayload(data) is { } pst && _stores.TryGetValue(pst.Path, out var store) && store.FindFolder(pst.SourceFolderNid) is { } folder)
        {
            var wanted = pst.Nids.ToHashSet();
            var all = _folderCache.TryGetValue((pst.Path, pst.SourceFolderNid), out var cached) && cached.Count > 0 ? cached : store.GetMessages(folder);
            var messages = all.Where(m => wanted.Contains(m.Nid)).ToList();
            return messages.Count == 0 ? null : new PstOrigin(pst.Path, folder, messages);
        }
        return null;
    }

    /// <summary>The drop itself: left button moves (Outlook's default), right button asks Move Here / Copy Here.</summary>
    private async Task DropAcrossStoresAsync(IDataObject data, object? nodeTag, Control anchor)
    {
        var target = TargetFromNodeTag(nodeTag);
        var origin = OriginFromDrag(data);
        if (target is null || origin is null) return;
        if (_messageDragRightButton)
        {
            _messageDragRightButton = false;
            var menu = new MenuFlyout();
            var moveHere = new MenuItem { Header = "Move Here" };
            var copyHere = new MenuItem { Header = "Copy Here" };
            moveHere.Click += async (_, _) => { if (await CheckOriginAllowsAsync(origin, copy: false)) await ExecuteTransferAsync(origin, target, copy: false); };
            copyHere.Click += async (_, _) => await ExecuteTransferAsync(origin, target, copy: true);
            menu.Items.Add(moveHere);
            menu.Items.Add(copyHere);
            menu.ShowAt(anchor);
        }
        else if (await CheckOriginAllowsAsync(origin, copy: false)) await ExecuteTransferAsync(origin, target, copy: false);
    }

    // ---- sources ------------------------------------------------------------------------------------------------------------------------

    private (ITransferSource Source, List<TransferItem> Items) MakeTransferSource(TransferOrigin origin)
    {
        switch (origin)
        {
            case PstOrigin p:
            {
                var store = _stores[p.Path];
                var byId = p.Messages.ToDictionary(m => m.Nid.ToString("x8"));
                var items = p.Messages.Select(m => new TransferItem(m.Nid.ToString("x8"), m.Subject, m.IsRead)).ToList();
                return (new PstTransferSource(store, byId), items);
            }
            case MicrosoftOrigin m:
            {
                var items = m.Messages.Select(x => new TransferItem(x.Id, x.Subject, x.IsRead)).ToList();
                return (new MicrosoftTransferSource(this, m.Folder.Account), items);
            }
            case GmailOrigin g:
            {
                var items = g.Messages.Select(x => new TransferItem(x.Id, x.Subject, x.IsRead)).ToList();
                return (new GmailTransferSource(GetGmailMailbox(g.Folder.Account), g.Folder.LabelId), items);
            }
        }
        throw new InvalidOperationException("Unknown source.");
    }

    private ITransferSink MakeTransferSink(TransferTarget target) => target switch
    {
        PstTarget p => new PstTransferSink(_stores[p.Path], p.Folder),
        MicrosoftTarget m => new MicrosoftTransferSink(this, m.Account, m.Folder.Id),
        GmailTarget g => new GmailTransferSink(GetGmailMailbox(g.Account), g.Folder.LabelId),
        _ => throw new InvalidOperationException("Unknown destination.")
    };

    private sealed class MicrosoftTransferSource(MainWindow w, ConnectedAccount account) : ITransferSource
    {
        public async Task<byte[]> ReadMimeAsync(TransferItem item, CancellationToken ct)
        {
            var token = await w.GetMicrosoftSession(account).GetAccessTokenAsync(ct);
            return await new GraphInboxReader(w._graphHttp, account.AccountId).GetMessageMimeAsync(token, item.Id, ct);
        }

        /// <summary>The originals go to the account's Deleted Items (recoverable), like Delete does; nothing is erased permanently.</summary>
        public async Task RemoveAsync(IReadOnlyList<TransferItem> items, CancellationToken ct)
        {
            var writer = new GraphMailWriter(w._graphHttp, account.AccountId);
            var token = await w.GetMicrosoftSession(account).GetAccessTokenAsync(ct);
            foreach (var item in items) await writer.MoveAsync(token, item.Id, "deleteditems", ct);
        }
    }

    private sealed class GmailTransferSource(GmailMailbox box, string labelId) : ITransferSource
    {
        public Task<byte[]> ReadMimeAsync(TransferItem item, CancellationToken ct) => box.GetRawAsync(item.Id, ct);

        /// <summary>The originals go to Gmail's Trash (emptied by Gmail after 30 days); nothing is erased permanently.</summary>
        public Task RemoveAsync(IReadOnlyList<TransferItem> items, CancellationToken ct) => box.TrashAsync(items.Select(i => i.Id).ToList(), ct);
    }

    // ---- sinks --------------------------------------------------------------------------------------------------------------------------

    private sealed class MicrosoftTransferSink(MainWindow w, ConnectedAccount account, string folderId) : ITransferSink
    {
        public async Task AddAsync(byte[] mime, TransferItem item, CancellationToken ct)
        {
            var token = await w.GetMicrosoftSession(account).GetAccessTokenAsync(ct);
            await new GraphMailWriter(w._graphHttp, account.AccountId).ImportMimeAsync(token, folderId, mime, item.IsRead, ct);
        }
        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;                 // each message is created on the server as it is added
    }

    private sealed class GmailTransferSink(GmailMailbox box, string labelId) : ITransferSink
    {
        public Task AddAsync(byte[] mime, TransferItem item, CancellationToken ct) => box.ImportAsync(mime, [labelId], item.IsRead, ct);
        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
