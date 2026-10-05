using OpenOutlook.Auth;
using OpenOutlook.Providers.Google;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Desktop;

/// <summary>What the compose window edits, independent of the mail provider.</summary>
public sealed record ComposeDraft(string To, string Cc, string Bcc, string Subject, string Body, bool Html);

/// <summary>An attachment in the compose window: a file on disk, bytes already in memory (a forwarded attachment), or one that lives on the server.</summary>
public sealed class ComposeFile
{
    public required string Name { get; init; }
    public string? Path { get; init; }
    public byte[]? Data { get; init; }
    public string? ServerId { get; init; }
    public long Size { get; init; }
    public string MimeType { get; init; } = "application/octet-stream";
    /// <summary>True once the provider has it (Microsoft: uploaded to the draft; Gmail: part of the saved draft).</summary>
    public bool Saved { get; set; }
    public override string ToString() => $"{Name} ({Size / 1024.0:0.#} KB)" + (Saved ? "" : " · not saved yet");
}

/// <summary>Everything a new message starts with: a reply, a forward, or nothing.</summary>
public sealed record ComposeSeed(string To = "", string Cc = "", string Subject = "", string Body = "", bool Html = false,
    string? InReplyTo = null, string? References = null, string? ThreadId = null, IReadOnlyList<ComposeFile>? Files = null);

public sealed record LoadedDraft(ComposeDraft Draft, IReadOnlyList<ComposeFile> Files);
public sealed record SavedDraft(string DraftId, IReadOnlyList<ComposeFile>? ServerFiles);

/// <summary>One sending account of the compose window's From list.</summary>
public sealed record ComposeAccount(string Address, string Kind, string? DisabledReason, Func<IComposeBackend> CreateBackend)
{
    public bool CanSend => DisabledReason is null;
    public override string ToString() => Address + (CanSend ? "" : "  (" + DisabledReason + ")");
}

/// <summary>The provider-specific half of composing: saving a draft, sending, loading a saved draft.</summary>
public interface IComposeBackend
{
    string Address { get; }
    string Kind { get; }
    long MaxAttachmentBytes { get; }
    /// <summary>Whether a message saved earlier can be opened again for editing.</summary>
    bool CanReopenDrafts { get; }
    string SavedMessage { get; }
    Task<LoadedDraft> LoadAsync(string draftId, CancellationToken cancellationToken = default);
    Task<SavedDraft> SaveAsync(string? draftId, ComposeDraft draft, IReadOnlyList<ComposeFile> files, IProgress<string> progress, CancellationToken cancellationToken = default);
    Task SendAsync(string? draftId, ComposeDraft draft, IReadOnlyList<ComposeFile> files, IProgress<string> progress, CancellationToken cancellationToken = default);
    Task RemoveServerFileAsync(string draftId, string serverId, CancellationToken cancellationToken = default);
}

/// <summary>Microsoft: the message is a server-side draft; attachments are uploaded to it; sending sends the draft.</summary>
public sealed class GraphComposeBackend(string address, GraphMailWriter writer, Func<Task<string>> token) : IComposeBackend
{
    public string Address => address;
    public string Kind => "Microsoft";
    public long MaxAttachmentBytes => GraphMailWriter.MaximumComposeAttachmentBytes;
    public bool CanReopenDrafts => true;
    public string SavedMessage => "Draft saved in your Microsoft mailbox.";

    public async Task<LoadedDraft> LoadAsync(string draftId, CancellationToken ct = default)
    {
        var t = await token();
        var draft = await writer.GetDraftAsync(t, draftId, ct);
        var files = new List<ComposeFile>();
        if (draft.HasAttachments)
            files.AddRange((await writer.ListDraftAttachmentsAsync(t, draftId, ct)).Select(a => new ComposeFile { Name = a.Name, ServerId = a.Id, Size = a.SizeBytes, Saved = true }));
        return new LoadedDraft(new ComposeDraft(draft.To, draft.Cc, draft.Bcc, draft.Subject, draft.Body, draft.ContentType.Equals("html", StringComparison.OrdinalIgnoreCase)), files);
    }

    public async Task<SavedDraft> SaveAsync(string? draftId, ComposeDraft d, IReadOnlyList<ComposeFile> files, IProgress<string> progress, CancellationToken ct = default)
    {
        var t = await token();
        var content = new GraphMailWriter.DraftContent(d.To, d.Cc, d.Bcc, d.Subject, d.Body, d.Html ? "HTML" : "Text");
        progress.Report("Saving draft…");
        var id = draftId is null ? await writer.CreateDraftAsync(t, content, ct) : draftId;
        if (draftId is not null) await writer.UpdateDraftAsync(t, id, content, ct);
        var uploaded = false;
        foreach (var pending in files.Where(f => !f.Saved && f.Path is not null))
        {
            progress.Report("Uploading " + pending.Name + "…");
            await writer.AddFileAttachmentAsync(t, id, pending.Path!, ct);
            uploaded = true;
        }
        IReadOnlyList<ComposeFile>? server = null;
        if (uploaded)
            server = (await writer.ListDraftAttachmentsAsync(t, id, ct)).Select(a => new ComposeFile { Name = a.Name, ServerId = a.Id, Size = a.SizeBytes, Saved = true }).ToList();
        return new SavedDraft(id, server);
    }

    public async Task SendAsync(string? draftId, ComposeDraft d, IReadOnlyList<ComposeFile> files, IProgress<string> progress, CancellationToken ct = default)
    {
        var saved = await SaveAsync(draftId, d, files, progress, ct);
        progress.Report("Sending…");
        await writer.SendDraftAsync(await token(), saved.DraftId, ct);
    }

    public async Task RemoveServerFileAsync(string draftId, string serverId, CancellationToken ct = default) =>
        await writer.RemoveAttachmentAsync(await token(), draftId, serverId, ct);
}

/// <summary>Gmail: the message is built as MIME on this computer; "Save draft" stores it as a Gmail draft, "Send" sends it (in the same conversation for a reply).</summary>
public sealed class GmailComposeBackend(string address, GmailMailbox box, ComposeSeed? seed = null) : IComposeBackend
{
    public string Address => address;
    public string Kind => "Gmail";
    public long MaxAttachmentBytes => GmailMimeBuilder.MaxAttachmentBytes;
    public bool CanReopenDrafts => false;
    public string SavedMessage => "Draft saved in Gmail.";

    public Task<LoadedDraft> LoadAsync(string draftId, CancellationToken ct = default) =>
        throw new NotSupportedException("Editing a saved Gmail draft is not available yet.");

    private byte[] Build(ComposeDraft d, IReadOnlyList<ComposeFile> files)
    {
        var m = new GmailOutgoing { From = address, To = d.To, Cc = d.Cc, Bcc = d.Bcc, Subject = d.Subject, InReplyTo = seed?.InReplyTo, References = seed?.References };
        if (d.Html) { m.Html = d.Body; m.Text = HtmlToText(d.Body); } else m.Text = d.Body;
        foreach (var f in files)
        {
            var data = f.Data ?? (f.Path is not null ? File.ReadAllBytes(f.Path) : throw new IOException("An attachment is no longer available: " + f.Name));
            m.Attachments.Add(new GmailOutgoingAttachment(f.Name, f.MimeType, data));
        }
        return GmailMimeBuilder.Build(m);
    }

    public async Task<SavedDraft> SaveAsync(string? draftId, ComposeDraft d, IReadOnlyList<ComposeFile> files, IProgress<string> progress, CancellationToken ct = default)
    {
        progress.Report("Saving draft…");
        var id = await box.SaveDraftAsync(draftId, await Task.Run(() => Build(d, files), ct), seed?.ThreadId, ct);
        foreach (var f in files) f.Saved = true;
        return new SavedDraft(id, null);
    }

    public async Task SendAsync(string? draftId, ComposeDraft d, IReadOnlyList<ComposeFile> files, IProgress<string> progress, CancellationToken ct = default)
    {
        var mime = await Task.Run(() => Build(d, files), ct);
        progress.Report("Sending…");
        if (draftId is null) await box.SendAsync(mime, seed?.ThreadId, ct);
        else
        {
            await box.SaveDraftAsync(draftId, mime, seed?.ThreadId, ct);       // bring the draft up to date, then send it
            await box.SendDraftAsync(draftId, ct);
        }
    }

    public Task RemoveServerFileAsync(string draftId, string serverId, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>The plain-text alternative of an HTML message.</summary>
    internal static string HtmlToText(string html)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(html, "<(br|/p|/div|/li|/h[1-6])[^>]*>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", "");
        return System.Net.WebUtility.HtmlDecode(text).Trim();
    }
}
