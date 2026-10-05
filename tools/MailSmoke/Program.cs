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
