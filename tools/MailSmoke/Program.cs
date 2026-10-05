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

if (step == "renderlive" && mAcc is not null)
{
    // renderlive <subject text>: the Hotmail message's HTML through the reader's sanitiser and snapshot renderer
    var r0 = new GraphInboxReader(graphHttp, mAcc.AccountId);
    var t0 = await mSession!.GetAccessTokenAsync();
    var msg = (await r0.GetInboxAsync(t0)).Messages.FirstOrDefault(m => m.Subject.Contains(args[1], StringComparison.OrdinalIgnoreCase));
    if (msg is null) { Console.WriteLine("not in the first 50 messages of the inbox"); return 1; }
    var body = await r0.GetMessageBodyAsync(t0, msg.Id);
    Console.WriteLine($"{msg.Subject}: {body?.ContentType}, {body?.Content.Length} chars");
    try
    {
        var interactive = SafeHtmlDocument.BuildInteractive(body!.Content, new Dictionary<string, byte[]>());
        Console.WriteLine("interactive document: " + interactive.Length);
        var doc = SafeHtmlDocument.Build(body.Content, new Dictionary<string, byte[]>());
        Console.WriteLine("sanitized document: " + doc.Length);
        var r = await BrowserHtmlRenderer.RenderDocumentAsync(doc, 800, CancellationToken.None, trustedOriginal: false);
        Console.WriteLine("rendered pages: " + r.Pages.Count);
        foreach (var png in r.Pages) Console.WriteLine($"  page {System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16))} x {System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20))}, {png.Length} bytes");
    }
    catch (Exception ex) { Console.WriteLine("FAILED: " + ex); }
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
