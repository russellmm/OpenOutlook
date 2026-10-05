using System;
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

    private async Task OpenGmailMessageAsync(GmailFolderSelection folder, GraphInboxMessage message, long version)
    {
        StatusText.Text = "Reading Gmail message…";
        try
        {
            var cancellationToken = _onlineCancellation?.Token ?? CancellationToken.None;
            var content = await GetGmailMailbox(folder.Account).GetContentAsync(message.Id, cancellationToken);
            if (version != _messageVersion || cancellationToken.IsCancellationRequested) return;
            _activeGraphMessage = null;
            _currentGraphAttachments = null;
            ExportAttachmentButton.IsEnabled = false;
            SubjectText.Text = message.Subject;
            SenderText.Text = message.From;
            RecipientText.Text = $"To   {message.To}";
            MessageDateText.Text = message.Received?.ToLocalTime().ToString("ddd M/d/yyyy h:mm tt") ?? "";
            SetReaderAvatar(message.From);
            ReaderReplyButton.IsVisible = ReaderReplyAllButton.IsVisible = ReaderForwardButton.IsVisible = false;   // read-only account
            AttachmentText.Text = content.Attachments.Count == 0 ? "" :
                $"Attachments: {string.Join(", ", content.Attachments.Select(a => a.FileName))} (saving Gmail attachments is not available yet)";
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

    /// <summary>Mark read / unread, star, archive and trash for the selected Gmail messages. Returns false when the current folder is not a Gmail one.</summary>
    private async Task<bool> TryHandleGmailActionAsync(string action)
    {
        if (_activeGmailFolder is not { } folder) return false;
        var account = folder.Account;
        if (action is "new" or "reply" or "replyAll" or "forward" or "editDraft")
        {
            StatusText.Text = "Composing, replying and forwarding are not available for Gmail yet.";
            return true;
        }
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
        if (_activeGmailFolder is not null || _activePath is not null) await MoveViaDialogAsync(copy: false);
        else StatusText.Text = "Select messages in an archive or a Gmail folder to move them. Moving Microsoft mailbox messages is planned.";
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
        // Sent, Drafts, Starred and Important are not places to move mail to.
        var destinations = (root?.Items.OfType<TreeViewItem>().Select(i => i.Tag).OfType<GmailFolderSelection>() ?? [])
            .Where(d => d.LabelId != folder.LabelId && d.LabelId is not ("SENT" or "DRAFT" or "STARRED" or "IMPORTANT")).ToList();
        if (destinations.Count == 0) { StatusText.Text = "There is no other Gmail folder to move into."; return; }

        var dialog = new Window
        {
            Title = $"{(copy ? "Add label to" : "Move")} {(messages.Length == 1 ? "message" : $"{messages.Length} messages")}",
            Width = 380, Height = 360, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var list = new ListBox();
        foreach (var d in destinations) list.Items.Add(new ListBoxItem { Content = d.Name, Tag = d });
        var ok = new Button { Content = copy ? "Add label" : "Move", IsEnabled = false };
        var cancel = new Button { Content = "Cancel" };
        list.SelectionChanged += (_, _) => ok.IsEnabled = list.SelectedItem is not null;
        ok.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(14), Spacing = 10, Children =
            {
                list,
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { ok, cancel } }
            }
        };
        if (!await dialog.ShowDialog<bool>(this) || list.SelectedItem is not ListBoxItem { Tag: GmailFolderSelection dest }) return;
        if (_gmailActionBusy) return;
        _gmailActionBusy = true;
        try
        {
            var ids = messages.Select(m => m.Id).ToArray();
            var box = GetGmailMailbox(account);
            // System labels the current folder cannot lose (Spam can; Trash and the others are handled by their own call).
            var removeCurrent = !copy && folder.LabelId is not ("SENT" or "DRAFT" or "STARRED" or "IMPORTANT" or "TRASH");
            if (dest.LabelId == GmailMailbox.TrashLabel) await box.TrashAsync(ids);
            else await box.ModifyLabelsAsync(ids, [dest.LabelId], removeCurrent ? [folder.LabelId] : []);
            var leaves = dest.LabelId == GmailMailbox.TrashLabel || removeCurrent;
            if (leaves && _activeGmailFolder == folder) foreach (var message in messages) ApplyCompletedMailAction(message, "delete");
            StatusText.Text = copy ? $"Added the label \"{dest.Name}\"." :
                !removeCurrent && dest.LabelId != GmailMailbox.TrashLabel ? $"Added the label \"{dest.Name}\" ({folder.Name} cannot be removed from these messages)." :
                messages.Length == 1 ? $"Message moved to {dest.Name}." : $"{messages.Length} messages moved to {dest.Name}.";
            _ = RefreshGmailCountsAsync(account);
        }
        catch (GmailReadException error) { StatusText.Text = error.Message; }
        catch (OperationCanceledException) { }
        catch (Exception) { StatusText.Text = "Gmail change failed. Check the connection and retry."; }
        finally { _gmailActionBusy = false; }
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
