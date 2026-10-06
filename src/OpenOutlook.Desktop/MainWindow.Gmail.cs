using System;
using Avalonia.Platform.Storage;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using OpenOutlook.Auth;
using OpenOutlook.Providers.Google;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Desktop;

/// <summary>
/// Gmail accounts in the folder tree (labels as folders) and in the message list / reading pane. Rows reuse the mailbox row type of the Microsoft accounts. Mark read,
/// star, archive, trash and move (label changes) need the gmail.modify scope; composing and sending are not available yet.
/// </summary>
public partial class MainWindow
{
    private sealed record GmailFolderSelection(ConnectedAccount Account, string LabelId, string Name);

    private GmailFolderSelection? _activeGmailFolder;
    private readonly Dictionary<string, GmailMailbox> _gmailBoxes = new(StringComparer.Ordinal);
    private readonly System.Net.Http.HttpClient _gmailHttp = GmailMailbox.CreateNoRedirectHttpClient();

    private static readonly (string Id, string Name)[] GmailSystemLabels =
    [
        ("INBOX", "Inbox"), ("STARRED", "Starred"), ("IMPORTANT", "Important"), ("SENT", "Sent"),
        ("DRAFT", "Drafts"), ("SPAM", "Spam"), ("TRASH", "Trash")
    ];

    private GmailMailbox GetGmailMailbox(ConnectedAccount account)
    {
        if (_gmailBoxes.TryGetValue(account.AccountId, out var box)) return box;
        var session = GetMicrosoftSession(account);              // the session class only refreshes OAuth tokens; it is provider-neutral
        return _gmailBoxes[account.AccountId] = new GmailMailbox(_gmailHttp, ct => new ValueTask<string>(session.GetAccessTokenAsync(ct)), account.DisplayAddress);
    }

    private void AddGmailAccountNodes()
    {
        foreach (var account in _accountRegistry.Load().Where(a => a.Provider == OAuthProvider.Google))
        {
            var root = new TreeViewItem { Header = account.DisplayAddress, Tag = account, IsExpanded = true };
            EnableFolderReordering(root);
            foreach (var (id, name) in GmailSystemLabels)
            {
                var item = new TreeViewItem { Header = name, Tag = new GmailFolderSelection(account, id, name) };
                EnableFolderReordering(item);
                root.Items.Add(item);
            }
            FolderTree.Items.Add(root);
            _ = LoadGmailLabelsAsync(account, root, _folderDiscoveryCancellation?.Token ?? CancellationToken.None);
        }
    }

    /// <summary>Adds the user's own labels and puts unread counts beside every folder.</summary>
    private async Task LoadGmailLabelsAsync(ConnectedAccount account, TreeViewItem root, CancellationToken cancellationToken)
    {
        try
        {
            var labels = await GetGmailMailbox(account).ListLabelsAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested || !FolderTree.Items.Contains(root)) return;
            foreach (var label in labels)
            {
                var existing = root.Items.OfType<TreeViewItem>().FirstOrDefault(i => i.Tag is GmailFolderSelection s && s.LabelId == label.Id);
                _gmailUnread[account.AccountId + "|" + label.Id] = label.Unread ?? 0;
                if (existing is not null) { existing.Header = FolderHeader(((GmailFolderSelection)existing.Tag!).Name, label.Unread ?? 0); continue; }
                if (label.IsSystem) continue;                       // CHAT, CATEGORY_*, ... are not folders
                var item = new TreeViewItem { Header = FolderHeader(label.Name, label.Unread ?? 0), Tag = new GmailFolderSelection(account, label.Id, label.Name) };
                EnableFolderReordering(item);
                root.Items.Add(item);
            }
            ApplyFolderOrder(root);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!cancellationToken.IsCancellationRequested && _activeGmailFolder is null && _activeMicrosoftAccount is null)
                StatusText.Text = "Could not list all Gmail labels. The standard folders are still available.";
        }
    }

    private async Task LoadGmailFolderAsync(GmailFolderSelection selection, long version, CancellationToken cancellationToken)
    {
        StatusText.Text = $"Loading {selection.Name}…";
        try
        {
            if (await ReadLocalFolderAsync(selection.Account, selection.LabelId) is { } local)        // the local copy answers without a network request
            {
                if (version != _folderVersion || cancellationToken.IsCancellationRequested) return;
                _currentGraphMessages = local.Messages;
                if (_graphRows is null) ShowGraphMessages(local.Messages);
                else ReconcileGraphMessages(local.Messages);
                StatusText.Text = $"{local.Messages.Count} newest of {local.TotalCount:N0} {selection.Name} messages · {selection.Account.DisplayAddress} · local copy";
                if (!_mirrorLastSync.TryGetValue(selection.Account.AccountId, out var lastSync) || DateTime.UtcNow - lastSync > TimeSpan.FromSeconds(90))
                    RequestMirrorSyncSoon(selection.Account);
                return;
            }
            var box = GetGmailMailbox(selection.Account);
            var ids = await box.ListLabelMessageIdsAsync(selection.LabelId, 100, cancellationToken);
            var summaries = await box.GetSummariesAsync(ids, cancellationToken);
            if (version != _folderVersion || cancellationToken.IsCancellationRequested) return;
            var messages = summaries.Select(ToGraphMessage).ToList();
            _currentGraphMessages = messages;
            ShowGraphMessages(messages);
            StatusText.Text = $"{messages.Count} newest {selection.Name} messages · {selection.Account.DisplayAddress} ";
        }
        catch (OperationCanceledException) { }
        catch (GmailReadException error)
        {
            if (version == _folderVersion) StatusText.Text = $"Could not load {selection.Name}: {error.Message}";
        }
        catch (Exception)
        {
            if (version == _folderVersion)
                StatusText.Text = $"Could not load {selection.Name}. Check the connection or use Account setup to sign in again.";
        }
    }

    private string? _chipChoice;
    private bool _openAttachment;   // the next attachment download is opened in its default program instead of saved to a chosen folder

    /// <summary>One clickable chip per attachment under the message header; clicking saves that attachment to a new file. Null clears the row.</summary>
    private void ShowAttachmentChips(IEnumerable<(string Name, long Size, bool CanSave)>? items)
    {
        AttachmentChips.Children.Clear();
        var list = items?.ToList();
        AttachmentChips.IsVisible = list is { Count: > 0 };
        if (list is null) return;
        foreach (var (name, size, canSave) in list)
        {
            var chip = new Button
            {
                Content = $"📎 {name} ({(size < 1024 ? size + " B" : size < 1048576 ? (size / 1024.0).ToString("0.#") + " KB" : (size / 1048576.0).ToString("0.#") + " MB")})",
                Margin = new Avalonia.Thickness(0, 0, 6, 4), Padding = new Avalonia.Thickness(8, 3), FontSize = 12.5, IsEnabled = canSave
            };
            ToolTip.SetTip(chip, canSave ? "Click to open this attachment or save it to a new file" : "This attachment cannot be saved");
            var risky = AttachmentLauncher.IsRisky(name);
            var open = new MenuItem { Header = risky ? "Open (not allowed for this file type)" : "Open", IsEnabled = !risky };
            open.Click += (_, _) => { _chipChoice = name; _openAttachment = true; ExportAttachmentClicked(chip, new Avalonia.Interactivity.RoutedEventArgs()); };
            var save = new MenuItem { Header = "Save as…" };
            save.Click += (_, _) => { _chipChoice = name; _openAttachment = false; ExportAttachmentClicked(chip, new Avalonia.Interactivity.RoutedEventArgs()); };
            chip.Flyout = new MenuFlyout { Items = { open, save } };
            AttachmentChips.Children.Add(chip);
        }
    }

    private GmailContent? _gmailContent;
    private string? _gmailContentMessageId;

    private static bool CanSaveGmailAttachment(GmailAttachmentInfo a)
    {
        if (a.AttachmentId is null || a.SizeBytes < 0 || a.SizeBytes > GmailMimeBuilder.MaxAttachmentBytes) return false;
        try { PstAttachmentExporter.ValidateSuggestedFileName(a.FileName); return true; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>"Save Attachments" for the open Gmail message: choose one if there are several, choose a folder, download, write as a new file (never overwrites).</summary>
    private async Task SaveGmailAttachmentAsync(GmailFolderSelection folder, GmailContent content, string messageId)
    {
        var version = _messageVersion;
        var candidates = content.Attachments.Where(CanSaveGmailAttachment).ToArray();
        if (candidates.Length == 0) { StatusText.Text = "This message has no attachment that can be saved."; return; }
        GmailAttachmentInfo? attachment = candidates[0];
        var chosen = _chipChoice is null ? null : candidates.FirstOrDefault(c => c.FileName == _chipChoice);
        _chipChoice = null;
        if (chosen is not null) attachment = chosen;
        else if (candidates.Length > 1)
        {
            var chooser = new Window { Title = "Choose an attachment", Width = 450, Height = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var list = new ListBox();
            foreach (var item in candidates) list.Items.Add(new ListBoxItem { Content = $"{item.FileName} ({item.SizeBytes / 1024.0:0.#} KB)", Tag = item });
            var button = new Button { Content = "Save selected", Margin = new Avalonia.Thickness(8) };
            button.Click += (_, _) => chooser.Close((list.SelectedItem as ListBoxItem)?.Tag as GmailAttachmentInfo);
            chooser.Content = new DockPanel { Children = { list, button } };
            DockPanel.SetDock(button, Dock.Bottom);
            attachment = await chooser.ShowDialog<GmailAttachmentInfo?>(this);
        }
        var openIt = _openAttachment;
        _openAttachment = false;
        if (attachment is null || version != _messageVersion) return;
        if (openIt && AttachmentLauncher.IsRisky(attachment.FileName)) { StatusText.Text = "This file type can run code, so it is not opened from here. Use Save as… instead."; return; }
        string? directory;
        if (openIt) directory = AttachmentLauncher.NewDirectory();
        else
        {
            var folders = await SafePick.FoldersAsync(this, new Avalonia.Platform.Storage.FolderPickerOpenOptions
            { Title = "Choose where to save this attachment as a new file", AllowMultiple = false }, failure => StatusText.Text = failure);
            if (version != _messageVersion) return;
            directory = folders.FirstOrDefault()?.TryGetLocalPath();
        }
        if (directory is null) return;
        ExportAttachmentButton.IsEnabled = false;
        try
        {
            StatusText.Text = $"Downloading {attachment.FileName}…";
            var data = await GetGmailMailbox(folder.Account).GetAttachmentAsync(messageId, attachment.AttachmentId!, _onlineCancellation?.Token ?? CancellationToken.None);
            var target = System.IO.Path.Combine(directory, PstAttachmentExporter.ValidateSuggestedFileName(attachment.FileName));
            await using (var file = new System.IO.FileStream(target, System.IO.FileMode.CreateNew, System.IO.FileAccess.Write, System.IO.FileShare.None))
                await file.WriteAsync(data);
            StatusText.Text = openIt ? AttachmentLauncher.Launch(target) ?? "Opened " + attachment.FileName + "." : "Attachment saved to a new file.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Attachment save cancelled."; }
        catch (System.IO.IOException) { StatusText.Text = "Could not save the attachment: a file with that name already exists or the folder is not writable. Nothing was overwritten."; }
        catch (GmailReadException error) { StatusText.Text = error.Message; }
        catch (Exception) { StatusText.Text = "Could not save the attachment. Check the connection or choose another folder."; }
        finally { ExportAttachmentButton.IsEnabled = version == _messageVersion && content.Attachments.Any(CanSaveGmailAttachment); }
    }

    private async Task OpenGmailMessageAsync(GmailFolderSelection folder, GraphInboxMessage message, long version)
    {
        StatusText.Text = "Reading Gmail message…";
        try
        {
            var cancellationToken = _onlineCancellation?.Token ?? CancellationToken.None;
            var localBody = await ReadLocalBodyAsync(folder.Account, message.Id);
            if (localBody is not null)
            {
                // the copy holds the body: show it at once; attachments and reply data follow from Gmail
                if (version != _messageVersion || cancellationToken.IsCancellationRequested) return;
                _activeGraphMessage = null;
                _currentGraphAttachments = null;
                _gmailContent = null;
                _gmailContentMessageId = null;
                SubjectText.Text = message.Subject;
                SenderText.Text = message.From;
                RecipientText.Text = $"To   {message.To}";
                MessageDateText.Text = message.Received?.ToLocalTime().ToString("ddd M/d/yyyy h:mm tt") ?? "";
                SetReaderAvatar(message.From);
                ReaderReplyButton.IsVisible = ReaderReplyAllButton.IsVisible = ReaderForwardButton.IsVisible = false;      // back once Gmail has answered
                AttachmentText.Text = message.HasAttachments ? "Loading attachments…" : "";
                ShowAttachmentChips(null);
                HideFormatBar();
                SetMessageBody(localBody.Html, localBody.Html is null ? localBody.Text : "");
                StatusText.Text = "Gmail message opened from the local copy.";
                if (!message.IsRead && folder.Account.CanModifyGmail && _options.ReadPaneMarkOnView)
                    _ = MarkGmailReadAfterViewingAsync(folder, message, version);
            }
            var content = await GetGmailMailbox(folder.Account).GetContentAsync(message.Id, cancellationToken);
            if (version != _messageVersion || cancellationToken.IsCancellationRequested) return;
            var bodyShown = localBody is not null;
            _activeGraphMessage = null;
            _currentGraphAttachments = null;
            _gmailContent = content;
            _gmailContentMessageId = message.Id;
            ExportAttachmentButton.IsEnabled = content.Attachments.Any(CanSaveGmailAttachment);
            SubjectText.Text = message.Subject;
            SenderText.Text = message.From;
            RecipientText.Text = $"To   {message.To}";
            MessageDateText.Text = message.Received?.ToLocalTime().ToString("ddd M/d/yyyy h:mm tt") ?? "";
            SetReaderAvatar(message.From);
            ReaderReplyButton.IsVisible = ReaderReplyAllButton.IsVisible = ReaderForwardButton.IsVisible = folder.Account.CanSendGmail;   // reading-pane Reply / Reply All / Forward
            AttachmentText.Text = "";
            ShowAttachmentChips(content.Attachments.Select(a => (a.FileName, (long)a.SizeBytes, CanSaveGmailAttachment(a))));
            if (bodyShown) return;                                                          // the body is already on screen, from the copy
            HideFormatBar();
            SetMessageBody(content.Html, content.Html is null ? (content.Text ?? content.Snippet ?? "") : "");
            StatusText.Text = _richRuns is null ? "Gmail message opened." : "Gmail message opened in rich-text view.";
            if (!message.IsRead && folder.Account.CanModifyGmail && _options.ReadPaneMarkOnView)
                _ = MarkGmailReadAfterViewingAsync(folder, message, version);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        { if (version == _messageVersion) StatusText.Text = "Could not read this Gmail message. Try signing in again from Account setup."; }
    }

    // ---- actions (gmail.modify) ----

    private bool _gmailActionBusy;
    private readonly Dictionary<string, int> _gmailUnread = new(StringComparer.Ordinal);   // accountId|labelId -> unread count

    /// <summary>Mark read / unread, star, archive and trash for the selected Gmail messages. Returns false when the current folder is not a Gmail one.</summary>
    private async Task<bool> TryHandleGmailActionAsync(string action)
    {
        if (_activeGmailFolder is not { } folder) return false;
        var account = folder.Account;
        if (action is "new" or "reply" or "replyAll" or "forward") { await ComposeGmailAsync(folder, action); return true; }
        if (action == "editDraft") { StatusText.Text = "Editing a saved Gmail draft is not available yet."; return true; }
        if (action is not ("read" or "unread" or "flag" or "unflag" or "archive" or "delete"))
        {
            StatusText.Text = "That action is not available for Gmail yet.";
            return true;
        }
        if (!account.CanModifyGmail)
        {
            await ExplainMailActionAsync("This Gmail sign-in is read-only. Sign in again from Account setup to allow organizing mail.", account);
            return true;
        }
        var messages = MessageList.SelectedItems.OfType<GraphMessageListRow>().Select(r => r.Message).DistinctBy(m => m.Id).ToArray();
        if (messages.Length == 0) { StatusText.Text = "Select a Gmail message first."; return true; }
        if (_gmailActionBusy) return true;
        if (action == "archive" && folder.LabelId != GmailMailbox.InboxLabel)
        { StatusText.Text = "Archive takes messages out of the Inbox; this folder is not the Inbox."; return true; }
        if (action == "delete" && folder.LabelId == GmailMailbox.TrashLabel)
        { StatusText.Text = "Gmail removes messages from Trash by itself after 30 days; deleting them permanently is not available."; return true; }
        var ids = messages.Select(m => m.Id).ToArray();
        var box = GetGmailMailbox(account);
        _gmailActionBusy = true;
        try
        {
            switch (action)
            {
                case "read": await box.SetReadAsync(ids, true); break;
                case "unread": await box.SetReadAsync(ids, false); break;
                case "flag": await box.SetStarredAsync(ids, true); break;
                case "unflag": await box.SetStarredAsync(ids, false); break;
                case "archive": await box.ArchiveAsync(ids); break;
                case "delete": await box.TrashAsync(ids); break;
            }
            // The row changes are applied to the list that is still showing this folder.
            if (_activeGmailFolder == folder) foreach (var message in messages) ApplyCompletedMailAction(message, action);
            StatusText.Text = action switch
            {
                "delete" => messages.Length == 1 ? "Message moved to Trash." : $"{messages.Length} messages moved to Trash.",
                "archive" => messages.Length == 1 ? "Message archived." : $"{messages.Length} messages archived.",
                _ => "Gmail updated."
            };
            _ = RefreshGmailCountsAsync(account);
        }
        catch (GmailReadException error) { StatusText.Text = error.Message; }
        catch (OperationCanceledException) { }
        catch (Exception) { StatusText.Text = "Gmail change failed. Check the connection and retry."; }
        finally { _gmailActionBusy = false; }
        return true;
    }

    private async void RibbonMoveClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_activeGmailFolder is not null || _activeMicrosoftFolder is not null || _activePath is not null) await MoveViaDialogAsync(copy: false);
        else StatusText.Text = "Select messages in an archive or a mailbox folder to move them.";
    }

    private Task RefreshGmailCountsAsync(ConnectedAccount account)
    {
        var root = FolderTree.Items.OfType<TreeViewItem>().FirstOrDefault(i => i.Tag is ConnectedAccount a && a.AccountId == account.AccountId);
        return root is null ? Task.CompletedTask : LoadGmailLabelsAsync(account, root, CancellationToken.None);
    }

    /// <summary>"Move to Folder..." for Gmail: a move adds the destination label and removes the current one; "copy" only adds the label (Gmail messages can carry several).</summary>
    private async Task MoveGmailViaDialogAsync(bool copy)
    {
        if (_activeGmailFolder is not { } folder) return;
        var account = folder.Account;
        if (!account.CanModifyGmail)
        {
            await ExplainMailActionAsync("This Gmail sign-in is read-only. Sign in again from Account setup to allow organizing mail.", account);
            return;
        }
        var messages = MessageList.SelectedItems.OfType<GraphMessageListRow>().Select(r => r.Message).DistinctBy(m => m.Id).ToArray();
        if (messages.Length == 0) { StatusText.Text = "Select a Gmail message first."; return; }
        var root = FolderTree.Items.OfType<TreeViewItem>().FirstOrDefault(i => i.Tag is ConnectedAccount a && a.AccountId == account.AccountId);
        // Sent, Drafts, Starred and Important are not places to move mail to; user labels nest by "Parent/Child" like Gmail shows them.
        var hidden = new HashSet<string> { "SENT", "DRAFT", "STARRED", "IMPORTANT" };
        int Unread(GmailFolderSelection g) => _gmailUnread.TryGetValue(account.AccountId + "|" + g.LabelId, out var n) ? n : 0;
        var all = (root?.Items.OfType<TreeViewItem>().Select(i => i.Tag).OfType<GmailFolderSelection>() ?? []).ToList();
        var accountRoot = new FolderPickItem(account.DisplayAddress, null, selectable: false);
        foreach (var system in all.Where(g => GmailSystemLabels.Any(l => l.Id == g.LabelId) && !hidden.Contains(g.LabelId)))
            accountRoot.Children.Add(new FolderPickItem(system.Name, system, Unread(system), selectable: system.LabelId != folder.LabelId));
        var byPath = new Dictionary<string, FolderPickItem>(StringComparer.Ordinal);
        foreach (var user in all.Where(g => GmailSystemLabels.All(l => l.Id != g.LabelId)).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
        {
            var slash = user.Name.LastIndexOf('/');
            var item = new FolderPickItem(slash >= 0 ? user.Name[(slash + 1)..] : user.Name, user, Unread(user), selectable: user.LabelId != folder.LabelId);
            byPath[user.Name] = item;
            (slash >= 0 && byPath.TryGetValue(user.Name[..slash], out var parentItem) ? parentItem : accountRoot).Children.Add(item);
        }
        if (accountRoot.Children.Count == 0) { StatusText.Text = "There is no other Gmail folder to move into."; return; }

        var noun = messages.Length == 1 ? "item" : "items";
        var dialog = new FolderPickerWindow(copy ? "Add Label" : "Move Items", copy ? $"Add the label to the selected {noun}:" : $"Move the selected {noun} to:", [accountRoot], async parent =>
        {
            var parentLabel = (parent?.Tag as GmailFolderSelection) is { } p && GmailSystemLabels.All(l => l.Id != p.LabelId) ? p : null;
            var name = await PromptForFolderNameAsync(parentLabel?.Name ?? account.DisplayAddress);
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (name.Contains('/')) { StatusText.Text = "A folder name cannot contain a slash."; return null; }
            try
            {
                var created = await GetGmailMailbox(account).CreateLabelAsync(parentLabel is null ? name.Trim() : parentLabel.Name + "/" + name.Trim());
                await RefreshGmailCountsAsync(account);
                StatusText.Text = $"Folder \"{created.Name}\" created.";
                return new FolderPickItem(name.Trim(), new GmailFolderSelection(account, created.Id, created.Name));
            }
            catch (GmailReadException error) { StatusText.Text = error.Message; return null; }
            catch (ArgumentException) { StatusText.Text = "That is not a valid folder name."; return null; }
            catch (Exception) { StatusText.Text = "Could not create the folder. Check the connection and retry."; return null; }
        });
        await dialog.ShowDialog(this);
        if (dialog.Result?.Tag is not GmailFolderSelection dest) return;
        await MoveOnlineAsync(messages.Select(m => m.Id).ToArray(), dest, copy);
    }

    /// <summary>The label changes of a Gmail move / copy (picker and drag and drop both end here); returns the status text.</summary>
    private async Task<string> GmailMoveCoreAsync(GmailFolderSelection source, IReadOnlyList<string> ids, GmailFolderSelection dest, bool copy)
    {
        var account = source.Account;
        var box = GetGmailMailbox(account);
        // System labels the current folder cannot lose (Spam can; Trash and the others are handled by their own call).
        var removeCurrent = !copy && source.LabelId is not ("SENT" or "DRAFT" or "STARRED" or "IMPORTANT" or "TRASH");
        if (dest.LabelId == GmailMailbox.TrashLabel) await box.TrashAsync(ids);
        else await box.ModifyLabelsAsync(ids, [dest.LabelId], removeCurrent ? [source.LabelId] : []);
        _ = RefreshGmailCountsAsync(account);
        if (copy && dest.LabelId == GmailMailbox.TrashLabel) return "Gmail cannot keep a copy in Trash; the message was moved there.";
        return copy ? $"Added the label \"{dest.Name}\"." :
            !removeCurrent && dest.LabelId != GmailMailbox.TrashLabel ? $"Added the label \"{dest.Name}\" ({source.Name} cannot be removed from these messages)." :
            ids.Count == 1 ? $"Message moved to {dest.Name}." : $"{ids.Count} messages moved to {dest.Name}.";
    }

    private async Task ReloadGmailFolderAsync(GmailFolderSelection folder)
    {
        if (_activeGmailFolder != folder) return;
        _folderRefreshCancellation?.Cancel();
        _folderRefreshCancellation = new CancellationTokenSource();
        await LoadGmailFolderAsync(folder, Interlocked.Increment(ref _folderVersion), _folderRefreshCancellation.Token);
    }

    /// <summary>The sign-in settings (client ids and the Google client secret) are needed for token refresh as soon as the window starts, not only when the Accounts window opens.</summary>
    private static void LoadOAuthClientConfigurationAtStartup()
    {
        try { OAuthClientConfiguration.Load(); }
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException)
        { AppLog.Error("startup", ex, "could not read the sign-in configuration"); }
    }

    /// <summary>Viewing an unread message for the reading pane's wait time marks it read in Gmail (same option as for other mailboxes).</summary>
    private async Task MarkGmailReadAfterViewingAsync(GmailFolderSelection folder, GraphInboxMessage message, long version)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(_options.ReadPaneWaitSeconds, 0, 300)));
            if (version != _messageVersion || _activeGmailFolder != folder || _gmailActionBusy) return;
            await GetGmailMailbox(folder.Account).SetReadAsync([message.Id], true);
            if (_activeGmailFolder == folder) ApplyCompletedMailAction(message, "read");
            _ = RefreshGmailCountsAsync(folder.Account);
        }
        catch (Exception) { /* marking read is best effort; the message stays unread */ }
    }

    private static GraphInboxMessage ToGraphMessage(GmailSummary s) => new(
        s.Id, string.IsNullOrWhiteSpace(s.Subject) ? "(no subject)" : s.Subject, DisplayName(s.From), DisplayName(s.To),
        s.Date, s.SizeBytes, s.HasAttachments, !s.IsUnread, s.Snippet, s.IsStarred, s.IsDraft);

    /// <summary>"Ann Lee &lt;ann@example.org&gt;" -> "Ann Lee"; a bare address stays as it is.</summary>
    internal static string DisplayName(string address)
    {
        var lt = address.IndexOf('<');
        var name = lt > 0 ? address[..lt].Trim().Trim('"') : address.Trim();
        return name.Length > 0 ? name : address.Trim('<', '>', ' ');
    }
}
