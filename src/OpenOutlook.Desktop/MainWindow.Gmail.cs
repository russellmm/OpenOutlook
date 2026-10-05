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
/// Gmail accounts in the folder tree (labels as folders) and in the message list / reading pane. Read-only for now (the account is connected with the
/// gmail.readonly scope): rows reuse the mailbox row type of the Microsoft accounts, but no mail action is wired to them.
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
            StatusText.Text = $"{messages.Count} newest {selection.Name} messages · {selection.Account.DisplayAddress} · read-only";
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
            StatusText.Text = _richRuns is null ? "Gmail message opened read-only." : "Gmail message opened in rich-text view.";
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        { if (version == _messageVersion) StatusText.Text = "Could not read this Gmail message. Try signing in again from Account setup."; }
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
