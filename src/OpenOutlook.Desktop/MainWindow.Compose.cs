using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Ganss.Xss;
using OpenOutlook.Auth;
using OpenOutlook.Providers.Google;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Desktop;

/// <summary>
/// Opening the compose window for any account: the From list is built from every connected Microsoft and Gmail account, and Gmail replies, reply all and
/// forwards are prepared here (quoted text, recipients, thread headers, forwarded attachments).
/// </summary>
public partial class MainWindow
{
    /// <summary>All accounts the compose window can send from; accounts that cannot send yet are listed with the reason.</summary>
    private IReadOnlyList<ComposeAccount> BuildComposeAccounts(ComposeSeed? seed = null)
    {
        var list = new List<ComposeAccount>();
        IReadOnlyList<ConnectedAccount> accounts;
        try { accounts = _accountRegistry.Load(); }
        catch (Exception) { return list; }
        foreach (var account in accounts)
        {
            var acc = account;
            if (acc.Provider == OAuthProvider.MicrosoftConsumers)
            {
                var ok = acc.CanWriteMicrosoftMail && acc.CanSendMicrosoftMail;
                list.Add(new ComposeAccount(acc.DisplayAddress, "Microsoft", ok ? null : "sign in again to allow sending", () =>
                    new GraphComposeBackend(acc.DisplayAddress, new GraphMailWriter(_graphHttp, acc.AccountId), () => GetMicrosoftSession(acc).GetAccessTokenAsync())));
            }
            else if (acc.Provider == OAuthProvider.Google)
            {
                list.Add(new ComposeAccount(acc.DisplayAddress, "Gmail", acc.CanSendGmail ? null : "sign in again to allow sending", () =>
                    new GmailComposeBackend(acc.DisplayAddress, GetGmailMailbox(acc), seed)));
            }
        }
        return list;
    }

    /// <summary>Opens a compose window. The account that is showing is the default sender; the From list can change it until a draft has been saved.</summary>
    private void OpenCompose(ConnectedAccount? preferred, ComposeSeed? seed = null, string? draftId = null)
    {
        preferred ??= _activeGmailFolder?.Account ?? _activeMicrosoftAccount;
        if (preferred is null && _accountDefault.Load() is { } defaultId) preferred = _accountRegistry.Load().FirstOrDefault(x => x.AccountId == defaultId);
        var accounts = BuildComposeAccounts(seed);
        if (accounts.Count == 0)
        {
            StatusText.Text = "Connect a Microsoft or Gmail account (Account setup) to compose mail.";
            return;
        }
        var initial = accounts.FirstOrDefault(a => preferred is not null && a.Address == preferred.DisplayAddress && a.CanSend)
                      ?? accounts.FirstOrDefault(a => a.CanSend) ?? accounts[0];
        new ComposeWindow(accounts, initial, seed, draftId).Show(this);
    }

    private bool IsSelf(string address, ConnectedAccount account) =>
        address.Contains(account.DisplayAddress, StringComparison.OrdinalIgnoreCase);

    /// <summary>New message, reply, reply all or forward for the selected Gmail message.</summary>
    private async Task ComposeGmailAsync(GmailFolderSelection folder, string action)
    {
        var account = folder.Account;
        if (action == "new") { OpenCompose(account); return; }
        if (!account.CanSendGmail)
        {
            await ExplainMailActionAsync("This Gmail sign-in cannot send mail yet. Sign in again from Account setup to allow composing, replying and forwarding.", account);
            return;
        }
        if (MessageList.SelectedItem is not GraphMessageListRow { Message: var message })
        {
            StatusText.Text = "Select a Gmail message first.";
            return;
        }
        StatusText.Text = action == "forward" ? "Preparing the forward (downloading attachments)…" : "Preparing the reply…";
        try
        {
            OpenCompose(account, await BuildGmailSeedAsync(folder, message, action));
            StatusText.Text = "";
        }
        catch (GmailReadException error) { StatusText.Text = error.Message; }
        catch (Exception) { StatusText.Text = "Could not prepare the message. Check the connection and retry."; }
    }

    /// <summary>The starting point of a reply, reply all or forward of a Gmail message: recipients, subject, quoted text, thread headers, forwarded attachments.</summary>
    private async Task<ComposeSeed> BuildGmailSeedAsync(GmailFolderSelection folder, GraphInboxMessage message, string action)
    {
        var account = folder.Account;
        var box = GetGmailMailbox(account);
        var content = await box.GetContentAsync(message.Id);
        string Header(string name) => content.Headers.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value ?? "";
        var from = Header("From");
        var replyTo = Header("Reply-To") is { Length: > 0 } r ? r : from;
        var date = message.Received?.ToLocalTime().ToString("ddd, MMM d, yyyy 'at' h:mm tt") ?? Header("Date");
        var html = content.Html is not null;
        var quoted = QuoteGmailBody(content, from, Header("To"), Header("Subject"), date, action == "forward", html);
        var messageId = Header("Message-ID");
        var references = (Header("References") + " " + messageId).Trim();
        if (action == "forward")
        {
            var files = new List<ComposeFile>();
            foreach (var a in content.Attachments.Where(a => a.AttachmentId is not null))
            {
                if (a.SizeBytes > GmailMimeBuilder.MaxAttachmentBytes) continue;
                var data = await box.GetAttachmentAsync(message.Id, a.AttachmentId!);
                files.Add(new ComposeFile { Name = a.FileName, Data = data, Size = data.Length, MimeType = a.MimeType });
            }
            return new ComposeSeed(Subject: Prefix("Fw: ", "Fwd: ", message.Subject), Body: quoted, Html: html, Files: files);
        }
        var cc = "";
        if (action == "replyAll")
        {
            var replyAddress = ExtractAddress(replyTo);
            cc = string.Join(", ", new[] { Header("To"), Header("Cc") }.Where(x => x.Length > 0).SelectMany(SplitList)
                .Where(a => !IsSelf(a, account) && !a.Contains(replyAddress, StringComparison.OrdinalIgnoreCase)));
        }
        return new ComposeSeed(To: replyTo, Cc: cc, Subject: Prefix("Re: ", "RE: ", message.Subject), Body: quoted, Html: html,
            InReplyTo: messageId.Length > 0 ? messageId : null, References: references.Length > 0 ? references : null, ThreadId: content.ThreadId);
    }

    private static string Prefix(string prefix, string alt, string subject) =>
        subject.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || subject.StartsWith(alt, StringComparison.OrdinalIgnoreCase) ? subject : prefix + subject;

    internal static IEnumerable<string> SplitList(string list)
    {
        var current = new System.Text.StringBuilder();
        var quotes = false;
        foreach (var c in list)
        {
            if (c == '"') quotes = !quotes;
            if (c == ',' && !quotes) { yield return current.ToString().Trim(); current.Clear(); }
            else current.Append(c);
        }
        if (current.ToString().Trim().Length > 0) yield return current.ToString().Trim();
    }

    internal static string ExtractAddress(string address)
    {
        var lt = address.LastIndexOf('<');
        return lt >= 0 && address.EndsWith('>') ? address[(lt + 1)..^1] : address.Trim();
    }

    /// <summary>The quoted original under a reply, or the forwarded-message header block and body under a forward.</summary>
    internal static string QuoteGmailBody(GmailContent content, string from, string to, string subject, string date, bool forward, bool html)
    {
        var header = forward
            ? $"---------- Forwarded message ---------\nFrom: {from}\nDate: {date}\nSubject: {subject}\nTo: {to}\n"
            : $"On {date}, {from} wrote:";
        if (html)
        {
            var sanitizer = new HtmlSanitizer();
            var body = sanitizer.Sanitize(content.Html ?? "");
            var head = WebUtility.HtmlEncode(header).Replace("\n", "<br>");
            return forward
                ? $"<br><br><div>{head}</div><br>{body}"
                : $"<br><br><div>{head}</div><blockquote style=\"margin:0 0 0 .8ex;border-left:1px solid #ccc;padding-left:1ex\">{body}</blockquote>";
        }
        var text = (content.Text ?? content.Snippet ?? "").Replace("\r\n", "\n");
        return forward
            ? "\n\n" + header + "\n" + text
            : "\n\n" + header + "\n" + string.Join("\n", text.Split('\n').Select(l => "> " + l));
    }
}
