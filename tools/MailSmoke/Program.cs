// Live mail smoke test: sends labelled test messages between the owner's own connected accounts through the same compose
// backends the app uses (Gmail MIME, Microsoft Graph drafts), then checks they arrived. Usage: MailSmoke <step>
// Needs openoutlook-oauth.json next to the exe (copied by the build step in the handoff notes).
using OpenOutlook.Auth;
using OpenOutlook.Desktop;
using OpenOutlook.Providers.Google;
using OpenOutlook.Providers.Microsoft;

const string GmailAddress = "russellmarr2012@gmail.com";
const string HotmailAddress = "rmarrash@hotmail.com";
var stamp = DateTime.Now.ToString("HHmmss");
var step = args.Length > 0 ? args[0] : "all";

OAuthClientConfiguration.Load();   // applies the Google client secret
var accounts = new ConnectedAccountRegistry().Load();
var gAcc = accounts.FirstOrDefault(a => a.Provider == OAuthProvider.Google && a.DisplayAddress.Equals(GmailAddress, StringComparison.OrdinalIgnoreCase));
var mAcc = accounts.FirstOrDefault(a => a.Provider == OAuthProvider.MicrosoftConsumers && a.DisplayAddress.Equals(HotmailAddress, StringComparison.OrdinalIgnoreCase));
Console.WriteLine($"gmail account: {(gAcc is null ? "NOT CONNECTED" : "ok, canSend=" + gAcc.CanSendGmail)}; hotmail account: {(mAcc is null ? "NOT CONNECTED" : "ok, canSend=" + (mAcc.CanWriteMicrosoftMail && mAcc.CanSendMicrosoftMail))}");

var secrets = SecretStores.CreateDefault();
var tokenHttp = DesktopOAuth.CreateHttpClient();
var gHttp = GmailMailbox.CreateNoRedirectHttpClient();
var graphHttp = GraphInboxReader.CreateSecureHttpClient();
GmailMailbox? gBox = null;
MicrosoftMailSession? mSession = null;
if (gAcc is not null) { var s = new MicrosoftMailSession(gAcc, secrets, tokenHttp); gBox = new GmailMailbox(gHttp, ct => new ValueTask<string>(s.GetAccessTokenAsync(ct)), gAcc.DisplayAddress); }
if (mAcc is not null) mSession = new MicrosoftMailSession(mAcc, secrets, tokenHttp);

if (step == "rendergmail" && gBox is not null)
{
    // rendergmail <subject text>: a Gmail message's HTML through the reader's sanitiser and snapshot renderer
    var ids = await gBox.ListLabelMessageIdsAsync("INBOX", 100);
    var hit = (await gBox.GetSummariesAsync(ids)).FirstOrDefault(m => m.Subject.Contains(args[1], StringComparison.OrdinalIgnoreCase));
    if (hit is null) { Console.WriteLine("not in the newest 100 inbox messages"); return 1; }
    var gc = await gBox.GetContentAsync(hit.Id);
    Console.WriteLine($"{hit.Subject}: html {gc.Html?.Length}, text {gc.Text?.Length}");
    var gh = gc.Html ?? "";
    System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "last-message.html"), gh);
    try
    {
        var interactive = SafeHtmlDocument.BuildInteractive(gh, new Dictionary<string, byte[]>());
        Console.WriteLine("interactive document: " + interactive.Length);
        var doc = SafeHtmlDocument.Build(gh, new Dictionary<string, byte[]>());
        Console.WriteLine("sanitized document: " + doc.Length);
        var r = await BrowserHtmlRenderer.RenderDocumentAsync(doc, 800, CancellationToken.None, trustedOriginal: false);
        Console.WriteLine("rendered pages: " + r.Pages.Count);
        foreach (var png in r.Pages) Console.WriteLine($"  page {System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16))} x {System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20))}, {png.Length} bytes");
    }
    catch (Exception ex) { Console.WriteLine("FAILED: " + ex); }
    return 0;
}
if (step == "renderlive" && mAcc is not null)
{
    // renderlive <subject text>: the Hotmail message's HTML through the reader's sanitiser and snapshot renderer
    var r0 = new GraphInboxReader(graphHttp, mAcc.AccountId);
    var t0 = await mSession!.GetAccessTokenAsync();
    var msg = (await r0.GetInboxAsync(t0)).Messages.FirstOrDefault(m => m.Subject.Contains(args[1], StringComparison.OrdinalIgnoreCase) && (args.Length < 3 || m.From.Contains(args[2], StringComparison.OrdinalIgnoreCase)));
    if (msg is null) { Console.WriteLine("not in the first 50 messages of the inbox"); return 1; }
    var body = await r0.GetMessageBodyAsync(t0, msg.Id);
    Console.WriteLine($"{msg.Subject}: {body?.ContentType}, {body?.Content.Length} chars");
    System.IO.File.WriteAllText("F:/Claude/OpenOutlook/.local/last-live.html", body?.Content ?? "");
    {
        var imgs = SafeHtmlDocument.FindImages(body!.Content);
        Console.WriteLine("images: " + imgs.Count);
        using var rc = SafeRemoteImageLoader.CreateClient();
        int ok = 0, bad = 0;
        var reasons = new Dictionary<string, int>();
        foreach (var im in imgs.Take(60))
        {
            try
            {
                if (!im.ContactsExternalSite) { var d = SafeHtmlDocument.DecodeDataImage(im); if (d is null) throw new Exception("data image invalid"); ok++; continue; }
                var bytes = await SafeRemoteImageLoader.FetchAsync(im.Value, rc, CancellationToken.None);
                var declared = SafeInlineImage.Validate(bytes);
                ok++;
            }
            catch (Exception ex) { bad++; var key = ex.GetType().Name + ": " + ex.Message; reasons[key] = reasons.GetValueOrDefault(key) + 1; if (reasons[key] == 1) Console.WriteLine("  e.g. " + im.Value[..Math.Min(110, im.Value.Length)] + " -> " + key); }
        }
        Console.WriteLine($"images loaded {ok}, failed {bad}");
        // the same document the reading pane builds once the pictures have arrived, and how long the layout takes
        var loaded = new Dictionary<string, byte[]>();
        foreach (var im in imgs.Take(60).Where(i => i.ContactsExternalSite))
        {
            try { loaded[im.Key] = await SafeRemoteImageLoader.FetchAsync(im.Value, rc, CancellationToken.None); } catch (Exception) { }
        }
        var withImages = SafeHtmlDocument.Build(body.Content, loaded);
        Console.WriteLine($"document with {loaded.Count} pictures: {withImages.Length:N0} chars, pictures {loaded.Values.Sum(v => (long)v.Length):N0} bytes");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var rr = await BrowserHtmlRenderer.RenderDocumentAsync(withImages, 800, CancellationToken.None, trustedOriginal: false);
            Console.WriteLine($"layout with pictures: {rr.Pages.Count} page(s) in {sw.Elapsed.TotalSeconds:0.0}s");
            System.IO.File.WriteAllBytes("F:/Claude/OpenOutlook/.local/render_img.png", rr.Pages[0]);
        }
        catch (Exception ex) { Console.WriteLine($"layout with pictures FAILED after {sw.Elapsed.TotalSeconds:0.0}s: {ex.GetType().Name}: {ex.Message}"); }
        foreach (var kv in reasons) Console.WriteLine($"  {kv.Value} x {kv.Key}");
    }
    try
    {
        var interactive = SafeHtmlDocument.BuildInteractive(body!.Content, new Dictionary<string, byte[]>());
        Console.WriteLine("interactive document: " + interactive.Length);
        var doc = SafeHtmlDocument.Build(body.Content, new Dictionary<string, byte[]>());
        Console.WriteLine("sanitized document: " + doc.Length);
        var r = await BrowserHtmlRenderer.RenderDocumentAsync(doc, 800, CancellationToken.None, trustedOriginal: false);
        Console.WriteLine("rendered pages: " + r.Pages.Count);
        for (var pi = 0; pi < r.Pages.Count; pi++) System.IO.File.WriteAllBytes("F:/Claude/OpenOutlook/.local/render_" + pi + ".png", r.Pages[pi]);
        foreach (var png in r.Pages) Console.WriteLine($"  page {System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16))} x {System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20))}, {png.Length} bytes");
    }
    catch (Exception ex) { Console.WriteLine("FAILED: " + ex); }
    return 0;
}
if (step == "livegmail" && gBox is not null)
{
    // livegmail: mirrors the last month of Gmail into a test copy, then changes the "[OpenOutlook test" message in the copy (read, star) and checks Gmail follows
    var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "gmail-test");
    Directory.CreateDirectory(dir);
    var pstPath = System.IO.Path.Combine(dir, "copy.pst");
    using var pst = File.Exists(pstPath) ? OpenOutlook.PstNative.PstEngineFactory.Open(pstPath, true) : OpenOutlook.PstNative.PstEngineFactory.Create(pstPath, gAcc!.DisplayAddress);
    using var st = new OpenOutlook.Mirror.SyncStateStore(System.IO.Path.ChangeExtension(pstPath, ".sync"));
    var src = new OpenOutlook.Mirror.GmailMirrorSource(gBox, true);
    var opts = new OpenOutlook.Mirror.MirrorSyncOptions(1);
    var watch = System.Diagnostics.Stopwatch.StartNew();
    async Task<OpenOutlook.Mirror.MirrorSyncResult> Go(string label) { watch.Restart(); var r = await OpenOutlook.Mirror.MirrorSyncEngine.SyncAsync(src, pst, st, opts, null, CancellationToken.None); Console.WriteLine($"{label} ({watch.Elapsed.TotalSeconds:0.0}s): {r}"); return r; }
    await Go("first sync");
    Console.WriteLine("folders: " + string.Join(", ", pst.AllFolders().Select(f => f.Name + "(" + f.ContentCount + ")")));
    async Task<GmailSummary?> OnServer() { var ids = await gBox.ListMessageIdsAsync("INBOX", "after:2026/09/01", 200); return (await gBox.GetSummariesAsync(ids)).FirstOrDefault(m => m.Subject.Contains("[OpenOutlook test") && m.Subject.Contains("Gmail to self")); }
    var inbox = pst.AllFolders().First(f => f.Name == "Inbox");
    var local = pst.GetMessages(inbox).FirstOrDefault(m => m.Subject.Contains("[OpenOutlook test") && m.Subject.Contains("Gmail to self"));
    if (local is null) { Console.WriteLine("test message not in the copy's Inbox"); return 1; }
    var before = await OnServer();
    Console.WriteLine($"gmail before: unread={before!.IsUnread} starred={before.IsStarred}");
    pst.SetReadState(local, before.IsUnread);                     // toggle: unread -> read, read -> unread
    pst.SetFlagged(local, !before.IsStarred);
    await Go("after read/star change");
    var after = await OnServer();
    Console.WriteLine($"gmail after:  unread={after!.IsUnread} starred={after.IsStarred}  (expected unread={!before.IsUnread} starred={!before.IsStarred})");
    local = pst.GetMessages(inbox).First(m => m.Subject.Contains("Gmail to self") && m.Subject.Contains("[OpenOutlook test"));
    pst.SetReadState(local, !before.IsUnread);
    pst.SetFlagged(local, before.IsStarred);
    await Go("revert");
    var back = await OnServer();
    Console.WriteLine($"gmail reverted: unread={back!.IsUnread} starred={back.IsStarred}; copy scan findings {pst.Scan().Findings.Count}");
    return 0;
}
if (step == "livepush" && mAcc is not null)
{
    // livepush: syncs a test copy of the Hotmail mailbox, then changes one of the "[OpenOutlook test" messages in the copy (read, flag, move) and checks the server follows
    var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "push-test");
    Directory.CreateDirectory(dir);
    var pstPath = System.IO.Path.Combine(dir, "copy.pst");
    using var pst = File.Exists(pstPath) ? OpenOutlook.PstNative.PstEngineFactory.Open(pstPath, true) : OpenOutlook.PstNative.PstEngineFactory.Create(pstPath, mAcc.DisplayAddress);
    using var st = new OpenOutlook.Mirror.SyncStateStore(System.IO.Path.ChangeExtension(pstPath, ".sync"));
    var writer = new GraphMailWriter(graphHttp, mAcc.AccountId);
    var src = new OpenOutlook.Mirror.GraphMirrorSource(new GraphMailFolderReader(graphHttp, mAcc.AccountId), new GraphMailboxSyncReader(graphHttp, mAcc.AccountId), new GraphInboxReader(graphHttp, mAcc.AccountId), ct => mSession!.GetAccessTokenAsync(ct), writer);
    var opts = new OpenOutlook.Mirror.MirrorSyncOptions(1);
    async Task<OpenOutlook.Mirror.MirrorSyncResult> Go(string label) { var r = await OpenOutlook.Mirror.MirrorSyncEngine.SyncAsync(src, pst, st, opts, null, CancellationToken.None); Console.WriteLine($"{label}: {r}"); return r; }
    await Go("first sync");
    var reader = new GraphInboxReader(graphHttp, mAcc.AccountId);
    async Task<GraphInboxMessage?> OnServer(string folderId = "inbox") => (await reader.GetInboxAsync(await mSession!.GetAccessTokenAsync())).Messages.FirstOrDefault(m => m.Subject.Contains("[OpenOutlook test") && m.Subject.Contains("Gmail to Hotmail"));
    var inbox = pst.AllFolders().First(f => f.Name == "Inbox");
    var local = pst.GetMessages(inbox).FirstOrDefault(m => m.Subject.Contains("[OpenOutlook test") && m.Subject.Contains("Gmail to Hotmail"));
    if (local is null) { Console.WriteLine("test message not in the copy"); return 1; }
    var before = await OnServer();
    Console.WriteLine($"server before: read={before!.IsRead} flagged={before.IsFlagged}");
    pst.SetReadState(local, !before.IsRead);
    pst.SetFlagged(local, !before.IsFlagged);
    await Go("after read/flag change");
    var after = await OnServer();
    Console.WriteLine($"server after:  read={after!.IsRead} flagged={after.IsFlagged}  (expected read={!before.IsRead} flagged={!before.IsFlagged})");
    local = pst.GetMessages(inbox).First(m => m.Subject.Contains("Gmail to Hotmail") && m.Subject.Contains("[OpenOutlook test"));
    pst.SetReadState(local, before.IsRead);
    pst.SetFlagged(local, before.IsFlagged);
    await Go("revert");
    var reverted = await OnServer();
    Console.WriteLine($"server reverted: read={reverted!.IsRead} flagged={reverted.IsFlagged}");
    // move to another folder and back
    var target = pst.AllFolders().First(f => f.Name == "Active_emails");
    local = pst.GetMessages(inbox).First(m => m.Subject.Contains("Gmail to Hotmail") && m.Subject.Contains("[OpenOutlook test"));
    pst.MoveMessage(local, target);
    await Go("moved to Active_emails");
    var moved = pst.GetMessages(target).FirstOrDefault(m => m.Subject.Contains("Gmail to Hotmail") && m.Subject.Contains("[OpenOutlook test"));
    Console.WriteLine($"in the copy's Active_emails: {moved is not null}; still in server inbox: {(await OnServer()) is not null}");
    if (moved is not null) pst.MoveMessage(moved, inbox);
    await Go("moved back");
    Console.WriteLine($"back in server inbox: {(await OnServer()) is not null}; copy scan findings {pst.Scan().Findings.Count}");
    return 0;
}
if (step == "scanlocal")
{
    // scanlocal <copy of a mirror pst> : how long finding the local changes takes and what it finds (read-only)
    var pp = args[1];
    using var e2 = OpenOutlook.PstNative.PstEngineFactory.Open(pp);
    using var st2 = new OpenOutlook.Mirror.SyncStateStore(System.IO.Path.ChangeExtension(pp, ".sync"));
    var dl = e2.DeletedItemsFolder()!;
    var sw2 = System.Diagnostics.Stopwatch.StartNew();
    var sc = OpenOutlook.Mirror.MirrorPush.Scan(e2, st2, e2.FindFolder(dl.ParentNid)!, dl);
    Console.WriteLine($"scan: {sw2.Elapsed.TotalSeconds:0.0}s, messages in state {st2.MessageCount()}, changes {sc.Changes.Count}, new folders {sc.NewFolders.Count}, local only {sc.LocalOnlyMessages}");
    foreach (var g in sc.Changes.GroupBy(c => c.Kind)) Console.WriteLine($"  {g.Key}: {g.Count()}");
    return 0;
}
if (step == "launchtest")
{
    // launchtest [browser path]: starts the layout browser the way the reading pane does and prints everything the browser says when it fails
    var exe = args.Length > 1 ? args[1] : BrowserHtmlRenderer.FindBrowser();
    Console.WriteLine("browser: " + exe);
    try
    {
        await using var b = await PuppeteerSharp.Puppeteer.LaunchAsync(new PuppeteerSharp.LaunchOptions { ExecutablePath = exe, Headless = true, Timeout = 20000, DumpIO = true, Args = ["--disable-background-networking", "--disable-extensions"] });
        Console.WriteLine("launched, version " + await b.GetVersionAsync());
    }
    catch (Exception ex) { Console.WriteLine("FAILED: " + ex.Message + " / " + ex.InnerException?.Message); }
    return 0;
}
if (step == "render")
{
    // Diagnoses the reading pane's snapshot renderer: usage MailSmoke render
    var html = "<html><body><h1>Hello</h1><p>Plain paragraph</p></body></html>";
    if (args.Length > 2)
    {
        // render <copy of a pst> <subject text>: the first message whose subject contains the text
        using var e = OpenOutlook.PstNative.PstEngineFactory.Open(args[1]);
        var hit = e.AllFolders().SelectMany(f => e.GetMessages(f)).FirstOrDefault(m => m.Subject.Contains(args[2], StringComparison.OrdinalIgnoreCase));
        if (hit is null) { Console.WriteLine("no such message"); return 1; }
        var msg = e.OpenMessage(hit);
        Console.WriteLine($"message: {hit.Subject}; html {msg.BodyHtml?.Length}, text {msg.BodyText?.Length}");
        html = msg.BodyHtml ?? "";
    }
    try
    {
        var doc = SafeHtmlDocument.Build(html, new Dictionary<string, byte[]>());
        Console.WriteLine("sanitized document: " + doc.Length + " chars");
        var r = await BrowserHtmlRenderer.RenderDocumentAsync(doc, 800, CancellationToken.None, trustedOriginal: false);
        Console.WriteLine("rendered pages: " + r.Pages.Count + ", text: " + r.Text?.ToString()?.Length);
    }
    catch (Exception ex) { Console.WriteLine("FAILED: " + ex); }
    return 0;
}
if (step == "mirror" && mAcc is not null)
{
    // Mirrors the Hotmail mailbox into a PST under .local/mirror-test (never touches the server): usage MailSmoke mirror [folder]
    var dir = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "mirror-test");
    Directory.CreateDirectory(dir);
    var pstPath = Path.Combine(dir, OpenOutlook.Mirror.MirrorLocations.SafeFileName(mAcc.DisplayAddress) + ".pst");
    using var pst = File.Exists(pstPath) ? OpenOutlook.PstNative.PstEngineFactory.Open(pstPath, true) : OpenOutlook.PstNative.PstEngineFactory.Create(pstPath, mAcc.DisplayAddress);
    using var st = new OpenOutlook.Mirror.SyncStateStore(Path.ChangeExtension(pstPath, ".sync"));
    var src = new OpenOutlook.Mirror.GraphMirrorSource(new GraphMailFolderReader(graphHttp, mAcc.AccountId), new GraphMailboxSyncReader(graphHttp, mAcc.AccountId), new GraphInboxReader(graphHttp, mAcc.AccountId), ct => mSession!.GetAccessTokenAsync(ct));
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var res = await OpenOutlook.Mirror.MirrorSyncEngine.SyncAsync(src, pst, st, new OpenOutlook.Mirror.MirrorSyncOptions(), new Progress<OpenOutlook.Mirror.MirrorProgress>(p => { if (p.Phase != "messages") Console.WriteLine($"   {p.Phase} {p.Folder} {p.Done}/{p.Total}"); }), CancellationToken.None);
    Console.WriteLine($"{pstPath}\n   {res}\n   {watch.Elapsed.TotalSeconds:0.0}s; scan findings: {pst.Scan().Findings.Count}");
    return 0;
}
if (step == "attach" && mAcc is not null)
{
    var r = new GraphInboxReader(graphHttp, mAcc.AccountId);
    var t = await mSession!.GetAccessTokenAsync();
    foreach (var m in (await r.GetInboxAsync(t)).Messages.Where(m => m.HasAttachments).Take(5))
    {
        try { var l = await new GraphAttachmentReader(graphHttp, mAcc.AccountId).ListAsync(t, m.Id); Console.WriteLine($"{m.Subject}: {l.Count} attachments: " + string.Join(", ", l.Select(x => x.Name))); }
        catch (Exception ex) { Console.WriteLine($"{m.Subject}: FAILED {ex.GetType().Name}: {ex.Message} / {ex.InnerException?.Message}"); }
    }
    return 0;
}
var attachPath = Path.Combine(Path.GetTempPath(), $"smoke-{stamp}.txt");
File.WriteAllText(attachPath, "OpenOutlook smoke test attachment " + stamp);
var progress = new Progress<string>(m => Console.WriteLine("   " + m));
int failures = 0;
async Task Run(string name, Func<Task> body)
{
    try { Console.WriteLine("== " + name); await body(); Console.WriteLine("   OK"); }
    catch (Exception ex) { failures++; Console.WriteLine("   FAILED: " + ex.GetType().Name + ": " + ex.Message); }
}
ComposeFile Attach() => new() { Name = Path.GetFileName(attachPath), Path = attachPath, Size = new FileInfo(attachPath).Length, MimeType = "text/plain" };
IComposeBackend GmailBackend(ComposeSeed? seed = null) => new GmailComposeBackend(gAcc!.DisplayAddress, gBox!, seed);
IComposeBackend HotmailBackend() => new GraphComposeBackend(mAcc!.DisplayAddress, new GraphMailWriter(graphHttp, mAcc.AccountId), () => mSession!.GetAccessTokenAsync());

if (gAcc is not null && gAcc.CanSendGmail && mAcc is not null)
    await Run("1. Gmail -> Hotmail (new message + attachment)", () => GmailBackend().SendAsync(null,
        new ComposeDraft(HotmailAddress, "", "", $"[OpenOutlook test {stamp}] Gmail to Hotmail", "Sent from the Gmail account by the smoke test.", false), [Attach()], progress));
if (mAcc is not null && mAcc.CanSendMicrosoftMail)
    await Run("2. Hotmail -> Gmail (new message + attachment)", () => HotmailBackend().SendAsync(null,
        new ComposeDraft(GmailAddress, "", "", $"[OpenOutlook test {stamp}] Hotmail to Gmail", "Sent from the Hotmail account by the smoke test.", false), [Attach()], progress));
if (gAcc is not null && gAcc.CanSendGmail)
    await Run("3. Gmail -> Gmail (to myself, with attachment)", () => GmailBackend().SendAsync(null,
        new ComposeDraft(GmailAddress, "", "", $"[OpenOutlook test {stamp}] Gmail to self", "Original message for reply and forward.", false), [Attach()], progress));

if (gBox is not null && gAcc!.CanSendGmail)
{
    GmailContent? original = null; string? originalId = null;
    await Run("4. wait for the self message, then reply (threaded)", async () =>
    {
        for (var i = 0; i < 12 && originalId is null; i++)
        {
            await Task.Delay(4000);
            foreach (var s in await gBox.GetSummariesAsync(await gBox.ListLabelMessageIdsAsync("INBOX", 15)))
                if (s.Subject.Contains($"[OpenOutlook test {stamp}] Gmail to self")) { originalId = s.Id; break; }
        }
        if (originalId is null) throw new Exception("the self message did not arrive within 48 s");
        original = await gBox.GetContentAsync(originalId);
        string H(string n) => original.Headers.FirstOrDefault(h => h.Name.Equals(n, StringComparison.OrdinalIgnoreCase))?.Value ?? "";
        var mid = H("Message-ID");
        var seed = new ComposeSeed(To: H("From"), Subject: "Re: " + H("Subject"), Body: "Reply from the smoke test.\n\n> Original message for reply and forward.",
            InReplyTo: mid, References: mid, ThreadId: original.ThreadId);
        await GmailBackend(seed).SendAsync(null, new ComposeDraft(seed.To, "", "", seed.Subject, seed.Body, false), [], progress);
        Console.WriteLine($"   replied in thread {original.ThreadId}");
    });
    await Run("5. forward with the original attachment to Hotmail", async () =>
    {
        if (original is null || originalId is null) throw new Exception("no original message (step 4 failed)");
        var files = new List<ComposeFile>();
        foreach (var a in original.Attachments.Where(a => a.AttachmentId is not null))
        {
            var data = await gBox.GetAttachmentAsync(originalId, a.AttachmentId!);
            files.Add(new ComposeFile { Name = a.FileName, Data = data, Size = data.Length, MimeType = a.MimeType });
            Console.WriteLine($"   downloaded attachment {a.FileName}: {data.Length} bytes");
        }
        if (files.Count == 0) throw new Exception("the original has no downloadable attachment");
        await GmailBackend().SendAsync(null, new ComposeDraft(HotmailAddress, "", "", $"Fwd: [OpenOutlook test {stamp}] Gmail to self",
            "---------- Forwarded message ---------\nOriginal message for reply and forward.", false), files, progress);
    });
    await Run("6. check the reply landed in the same Gmail thread", async () =>
    {
        for (var i = 0; i < 12; i++)
        {
            await Task.Delay(4000);
            var inThread = (await gBox.GetSummariesAsync(await gBox.ListLabelMessageIdsAsync("INBOX", 20))).Count(s => s.ThreadId == original!.ThreadId);
            Console.WriteLine($"   messages in the thread so far: {inThread}");
            if (inThread >= 2) return;
        }
        throw new Exception("the reply did not appear in the thread");
    });
}

if (mAcc is not null && mAcc.CanSendMicrosoftMail)
    await Run("7. Hotmail: reply to the message that came from Gmail", async () =>
    {
        var reader = new GraphInboxReader(graphHttp, mAcc.AccountId);
        var token = await mSession!.GetAccessTokenAsync();
        GraphInboxMessage? found = null;
        for (var i = 0; i < 12 && found is null; i++)
        {
            await Task.Delay(i == 0 ? 0 : 4000);
            found = (await reader.GetInboxAsync(token)).Messages.FirstOrDefault(m => m.Subject.Contains($"[OpenOutlook test {stamp}] Gmail to Hotmail"));
        }
        if (found is null) throw new Exception("the Gmail message did not reach the Hotmail inbox within 48 s");
        var writer = new GraphMailWriter(graphHttp, mAcc.AccountId);
        var draft = await writer.CreateResponseDraftAsync(token, found.Id, "reply");
        var d = await writer.GetDraftAsync(token, draft);
        Console.WriteLine($"   reply draft: To={d.To}, Subject={d.Subject}");
        await writer.UpdateDraftAsync(token, draft, new GraphMailWriter.DraftContent(d.To, d.Cc, d.Bcc, d.Subject, "Reply from the smoke test.", "Text"));
        await writer.SendDraftAsync(token, draft);
    });

File.Delete(attachPath);
Console.WriteLine(failures == 0 ? "ALL STEPS OK" : failures + " STEP(S) FAILED");
return failures;
