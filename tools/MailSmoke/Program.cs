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
var step = args.Length > 0 ? args[0] : "";

if (step == "mkbigrecips")
{
    // creates small PSTs, each with one message that has N recipients (long display-to), to find what SCANPST objects to: MailSmoke mkbigrecips <dir> [n n n ...]
    var outDir = args[1];
    Directory.CreateDirectory(outDir);
    var specs = args.Length > 2 ? args.Skip(2).ToArray() : new[] { "1", "60", "100", "130", "200" };       // "1:soloL" = one recipient whose name is L characters, "N" = N ordinary recipients, "N:short" = N tiny names, "N:fat" = N recipients with 120-character names
    var t0 = new DateTime(2026, 2, 13, 13, 0, 0, DateTimeKind.Utc);
    foreach (var spec in specs)
    {
        var parts = spec.Split(':');
        var n = int.Parse(parts[0]);
        var style = parts.Length > 1 ? parts[1] : "normal";
        var file = Path.Combine(outDir, $"recips_{spec.Replace(':', '_')}.pst");
        if (File.Exists(file)) File.Delete(file);
        using var e = OpenOutlook.PstNative.PstEngineFactory.Create(file, "probe " + n);
        var topNid = e.AllFolders().First(f => f.Name == "Deleted Items").ParentNid;       // the mailbox top folder
        var inbox = e.CreateFolder(topNid, "Inbox");
        PstCore.MailImport Msg(string subject, int recips)
        {
            var m = new PstCore.MailImport { Subject = subject, SenderName = "Sam Sender", SenderEmail = "sam@example.test", BodyText = "hello body", Sent = t0, Received = t0, MessageId = $"<{subject}@example.test>" };
            if (style.StartsWith("cc") || style.StartsWith("bcc"))          // one ordinary To recipient plus one Cc / Bcc recipient whose name is N characters
            {
                m.Recipients.Add(new PstCore.ImportRecipient("Plain Person", "plain@example.test", PstCore.RecipientKind.To));
                m.Recipients.Add(new PstCore.ImportRecipient(new string('n', int.Parse(style.TrimStart('b', 'c'))), "long@example.test", style.StartsWith("bcc") ? PstCore.RecipientKind.Bcc : PstCore.RecipientKind.Cc));
                return m;
            }
            for (var i = 0; i < recips; i++)
                m.Recipients.Add(style.StartsWith("solo") ? new PstCore.ImportRecipient(new string('n', int.Parse(style[4..])), "solo@example.test", PstCore.RecipientKind.To)
                    : style == "short" ? new PstCore.ImportRecipient("R" + i, $"r{i}@x.test", PstCore.RecipientKind.To)
                    : style == "fat" ? new PstCore.ImportRecipient("Recipient " + i + " " + new string('n', 110), $"recipient{i}@example.test", PstCore.RecipientKind.To)
                    : new PstCore.ImportRecipient("Recipient Number " + i, $"recipient{i}.mailbox@example-domain{i}.test", PstCore.RecipientKind.To));
            return m;
        }
        var res = e.ImportMessages(inbox, new List<PstCore.MailImport> { Msg("normal before", 2), Msg("many recipients", n), Msg("normal after", 2) });
        var shown = e.GetMessages(inbox).First(x => x.Subject == "many recipients");
        Console.WriteLine($"{file}: recipients {n} ({style}), display-to {shown.To.Length} chars ({shown.To.Length * 2} bytes UTF-16)");
    }
    return 0;
}

if (step == "mkmulti")
{
    // one PST with one message per given display-to length (one To recipient whose name has that many characters): MailSmoke mkmulti <file> L L L ...
    var file = args[1];
    if (File.Exists(file)) File.Delete(file);
    using var e = OpenOutlook.PstNative.PstEngineFactory.Create(file, "multi");
    var inbox = e.CreateFolder(e.AllFolders().First(f => f.Name == "Deleted Items").ParentNid, "Inbox");
    var t0 = new DateTime(2026, 2, 13, 13, 0, 0, DateTimeKind.Utc);
    var list = new List<PstCore.MailImport>();
    foreach (var l in args.Skip(2).Select(int.Parse))
    {
        var m = new PstCore.MailImport { Subject = "to-length " + l, SenderName = "Sam Sender", SenderEmail = "sam@example.test", BodyText = "hello body", Sent = t0.AddMinutes(-l), Received = t0.AddMinutes(-l), MessageId = $"<len{l}@example.test>" };
        m.Recipients.Add(new PstCore.ImportRecipient(new string('n', l), "r@example.test", PstCore.RecipientKind.To));
        list.Add(m);
    }
    e.ImportMessages(inbox, list);
    foreach (var x in e.GetMessages(inbox)) Console.WriteLine($"0x{x.Nid:X}	{x.Subject}	display-to {x.To.Length} chars");
    return 0;
}

if (step == "mkmany")
{
    // one PST with N messages, every one with its own subject (conversation topic), so the message index (node 0xE01) grows: MailSmoke mkmany <file> N
    var file = args[1];
    int count = int.Parse(args[2]);
    if (File.Exists(file)) File.Delete(file);
    using var e = OpenOutlook.PstNative.PstEngineFactory.Create(file, "many");
    var inbox = e.CreateFolder(e.AllFolders().First(f => f.Name == "Deleted Items").ParentNid, "Inbox");
    var t0 = new DateTime(2026, 2, 13, 13, 0, 0, DateTimeKind.Utc);
    for (int from = 0; from < count; from += 200)
    {
        var list = new List<PstCore.MailImport>();
        for (int i = from; i < Math.Min(count, from + 200); i++)
        {
            var m = new PstCore.MailImport { Subject = $"Message number {i} about topic {i * 7919 % 100003}", SenderName = "Sam Sender", SenderEmail = "sam@example.test", BodyText = "hello body " + i, Sent = t0.AddMinutes(-i), Received = t0.AddMinutes(-i), MessageId = $"<many{i}@example.test>" };
            m.Recipients.Add(new PstCore.ImportRecipient("Rae Receiver", "r@example.test", PstCore.RecipientKind.To));
            list.Add(m);
        }
        e.ImportMessages(inbox, list);
    }
    Console.WriteLine($"{e.GetMessages(inbox).Count()} messages");
    return 0;
}

if (step == "touchall")
{
    // rewrites the contents tables the way the application's actions do: flags and un-flags every message of the file, marks it read and unread again: MailSmoke touchall <file>
    using var e = OpenOutlook.PstNative.PstEngineFactory.OpenEditable(Path.GetFullPath(args[1]), out var reason);
    int n = 0;
    foreach (var f in e.AllFolders())
        foreach (var m in e.GetMessages(f).ToList())
        {
            e.SetFlagged(m, true); e.SetFlagged(m, false);
            e.SetReadState(m, !m.IsRead); e.SetReadState(m, m.IsRead);
            n++;
        }
    var dl = e.DeletedItemsFolder()!;
    foreach (var f in e.AllFolders().Where(x => x.Name == "Inbox").ToList())
        foreach (var m in e.GetMessages(f).ToList()) e.MoveMessage(m, dl);          // the junk cleaner's move: rows are rebuilt in another table
    Console.WriteLine($"touched {n} messages");
    return 0;
}

if (step == "colorprobe")
{
    // Offline, Windows. Does the embedded web view change colours? Shows bands of #D9D9D6, #FFFFFF and #006FCF (the AmEx mailing's frame,
    // card and logo) and reads the screen pixels back. Set WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS before running to compare flags.
    if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("colorprobe reads screen pixels with GDI: Windows only."); return 2; }
    var log = new List<string>();
    var ui = new Thread(() =>
    {
    Avalonia.AppBuilderDesktopExtensions.UsePlatformDetect(Avalonia.AppBuilder.Configure<Avalonia.Application>()).SetupWithoutStarting();
    var done = new CancellationTokenSource();
    Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
    {
        var view = new Avalonia.Controls.NativeWebView();
        var window = new Avalonia.Controls.Window { Width = 600, Height = 480, Title = "OpenOutlook colour probe", Content = view, Topmost = true,
            WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterScreen };
        var loaded = new TaskCompletionSource<bool>();
        view.NavigationCompleted += (_, _) => loaded.TrySetResult(true);
        window.Show();
        await Task.Delay(500);
        view.NavigateToString("<!doctype html><html><body style='margin:0'><div style='height:160px;background:#D9D9D6'></div>" +
            "<div style='height:160px;background:#FFFFFF'></div><div style='height:160px;background:#006FCF'></div></body></html>");
        await Task.WhenAny(loaded.Task, Task.Delay(8000));
        await Task.Delay(1500);
        var scaling = window.RenderScaling;
        foreach (var (name, y) in new[] { ("#D9D9D6 frame", 80), ("#FFFFFF card", 240), ("#006FCF logo", 400) })
        {
            var at = Avalonia.VisualExtensions.PointToScreen(window, new Avalonia.Point(300, y));
            var hdc = ColorProbe.GetDC(IntPtr.Zero);
            var c = ColorProbe.GetPixel(hdc, at.X, at.Y);
            ColorProbe.ReleaseDC(IntPtr.Zero, hdc);
            log.Add($"{name,-14} -> #{c & 0xFF:X2}{(c >> 8) & 0xFF:X2}{(c >> 16) & 0xFF:X2}");
        }
        log.Add("scaling " + scaling + ", WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=" + (Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS") ?? "(unset)"));
        window.Close();
        done.Cancel();
    });
    Avalonia.Threading.Dispatcher.UIThread.MainLoop(done.Token);
    });
    ui.SetApartmentState(ApartmentState.STA);
    ui.Start();
    ui.Join();
    foreach (var line in log) Console.WriteLine(line);
    return 0;
}
if (step == "wvprobe")
{
    // How long the embedded web view takes to load a page in the situations the reader puts it in (opens a real window for a few seconds): MailSmoke wvprobe
    var log = new List<string>();
    var ui = new Thread(() =>
    {
    Avalonia.AppBuilderDesktopExtensions.UsePlatformDetect(Avalonia.AppBuilder.Configure<Avalonia.Application>()).SetupWithoutStarting();
    var done = new CancellationTokenSource();
    Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
    {
        var view = new Avalonia.Controls.NativeWebView();
        var window = new Avalonia.Controls.Window { Width = 700, Height = 500, Title = "OpenOutlook web view probe", Content = view };
        TaskCompletionSource<bool>? waiting = null;
        var guard = new OpenOutlook.Desktop.OwnDocumentNavigation();
        view.NavigationCompleted += (_, e) => { log.Add("   completed: success=" + e.IsSuccess); waiting?.TrySetResult(true); };
        view.NavigationStarted += (_, e) =>
        {
            var uri = e.Request?.ToString() ?? "(null)";
            log.Add("   started: " + (uri.Length > 70 ? uri[..70] + "..." : uri) + " scheme=" + e.Request?.Scheme);
            if (args.Length > 1 && args[1] == "cancelnonabout" && e.Request is { Scheme: not "about" }) e.Cancel = true;     // what the reader's handler did before 2026-10-15
            if (args.Length > 1 && args[1] == "guard" && !guard.Allows(e.Request)) e.Cancel = true;                       // what it does now
        };
        async Task Try(string label, Action before)
        {
            before();
            waiting = new TaskCompletionSource<bool>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var marker = Guid.NewGuid().ToString("N");
            guard.Expect();
            view.NavigateToString("<!doctype html><html><head><meta name='m' content='" + marker + "'></head><body><p>" + label + " - some selectable text</p></body></html>");
            var finished = await Task.WhenAny(waiting.Task, Task.Delay(8000)) == waiting.Task;
            string probe;
            try { probe = (await view.InvokeScript("document.querySelector('meta[name=\"m\"]')?.content === '" + marker + "'")) ?? "null"; }
            catch (Exception ex) { probe = "probe threw " + ex.GetType().Name; }
            log.Add($"{label,-55} completed={finished,-5} after {sw.ElapsedMilliseconds,5} ms; marker found={probe}");
        }
        view.IsVisible = false;
        window.Show();
        await Task.Delay(500);
        await Try("1 hidden from the start (never shown yet)", () => { });
        await Try("2 made visible, then navigated", () => view.IsVisible = true);
        await Try("3 visible, navigated again", () => { });
        await Try("4 hidden again, then navigated (what the reader does)", () => view.IsVisible = false);
        await Try("5 still hidden, navigated again", () => { });
        await Try("6 visible again", () => view.IsVisible = true);
        await Try("7 opacity 0 instead of hidden", () => view.Opacity = 0);
        view.Opacity = 1;
        // a page script that tries to navigate itself to a data: document must be refused
        waiting = new TaskCompletionSource<bool>();
        try { await view.InvokeScript("location.href = 'data:text/html,<p>sneaky</p>'; 'ok'"); } catch { }
        await Task.WhenAny(waiting.Task, Task.Delay(1500));
        string still;
        try { still = (await view.InvokeScript("document.body ? document.body.innerText : ''")) ?? "null"; } catch (Exception ex) { still = ex.GetType().Name; }
        log.Add("script navigation to data: -> page now says: " + still);
        if (args.Length > 3)
        {
            // a real message, built the way the reader builds it (pictures loaded through the reader's loader)
            using var e = OpenOutlook.PstNative.PstEngineFactory.Open(Path.GetFullPath(args[2]));
            var hit = e.AllFolders().SelectMany(f => e.GetMessages(f)).First(m => m.Subject.Contains(args[3], StringComparison.OrdinalIgnoreCase));
            var html = e.OpenMessage(hit).BodyHtml;
            var images = new Dictionary<string, byte[]>();
            using var client = OpenOutlook.Desktop.SafeRemoteImageLoader.CreateClient();
            foreach (var source in SafeHtmlDocument.FindImages(html).Where(x => x.ContactsExternalSite))
                try { images[source.Key] = await OpenOutlook.Desktop.SafeRemoteImageLoader.FetchAsync(source.Value, client); } catch { }
            var doc = SafeHtmlDocument.BuildInteractive(html, images);
            log.Add($"message: {hit.Subject} - interactive document {doc.Length:N0} chars, {images.Count} pictures");
            waiting = new TaskCompletionSource<bool>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            view.IsVisible = true;
            guard.Expect();
            view.NavigateToString(doc);
            var finished = await Task.WhenAny(waiting.Task, Task.Delay(10000)) == waiting.Task;
            string text;
            try { text = (await view.InvokeScript("document.body ? document.body.innerText.length : -1")) ?? "null"; }
            catch (Exception ex) { text = "probe threw " + ex.GetType().Name; }
            log.Add($"real message                                            completed={finished,-5} after {sw.ElapsedMilliseconds,5} ms; body text length={text}");
            await Task.Delay(1500);
        }
        window.Close();
        done.Cancel();
    });
    Avalonia.Threading.Dispatcher.UIThread.MainLoop(done.Token);
    });
    if (OperatingSystem.IsWindows()) ui.SetApartmentState(ApartmentState.STA);       // WebView2 refuses to start on a multithreaded-apartment thread
    ui.Start();
    ui.Join();
    foreach (var line in log) Console.WriteLine(line);
    return 0;
}

if (step == "compare")
{
    // renders one message the way the reader does (sanitized + loaded pictures) and as the original HTML, side by side PNGs: MailSmoke compare <copy of a pst> <subject part> <out dir> [width]
    using var e = OpenOutlook.PstNative.PstEngineFactory.Open(Path.GetFullPath(args[1]));
    var hit = e.AllFolders().SelectMany(f => e.GetMessages(f)).FirstOrDefault(m => m.Subject.Contains(args[2], StringComparison.OrdinalIgnoreCase));
    if (hit is null) { Console.WriteLine("no such message"); return 1; }
    var html = e.OpenMessage(hit).BodyHtml;
    var outDir = Directory.CreateDirectory(args[3]).FullName;
    var width = args.Length > 4 ? int.Parse(args[4]) : 900;
    var images = new Dictionary<string, byte[]>();
    using var client = OpenOutlook.Desktop.SafeRemoteImageLoader.CreateClient();
    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(html, "(?:src|background)\\s*=\\s*[\"']([^\"']+)[\"']|url\\(\\s*['\"]?([^'\")]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
    {
        var raw = System.Net.WebUtility.HtmlDecode(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        if (!SafeHtmlDocument.TrySource(raw, out var source) || !source.ContactsExternalSite || images.ContainsKey(source.Key)) continue;
        try { images[source.Key] = await OpenOutlook.Desktop.SafeRemoteImageLoader.FetchAsync(source.Value, client); } catch { }
    }
    Console.WriteLine($"{hit.Subject}: {images.Count} pictures loaded");
    var safe = SafeHtmlDocument.Build(html, images);
    File.WriteAllText(Path.Combine(outDir, "sanitized.html"), safe);
    File.WriteAllText(Path.Combine(outDir, "original.html"), html);
    var ours = await BrowserHtmlRenderer.RenderDocumentAsync(safe, width, CancellationToken.None, trustedOriginal: false);
    for (var i = 0; i < ours.Pages.Count; i++) File.WriteAllBytes(Path.Combine(outDir, $"ours-{i}.png"), ours.Pages[i]);
    var orig = await BrowserHtmlRenderer.RenderDocumentAsync(html, width, CancellationToken.None, trustedOriginal: true);
    for (var i = 0; i < orig.Pages.Count; i++) File.WriteAllBytes(Path.Combine(outDir, $"original-{i}.png"), orig.Pages[i]);
    Console.WriteLine($"ours {ours.Pages.Count} page(s), original {orig.Pages.Count} page(s) -> {outDir}");
    return 0;
}

if (step == "imgsurvey")
{
    // the newest N messages of a folder of a (copy of a) PST: how many external pictures the reader's loader gets, and why the rest fail: MailSmoke imgsurvey <file> <folder> <N>
    using var e = OpenOutlook.PstNative.PstEngineFactory.Open(Path.GetFullPath(args[1]));
    var folder = e.AllFolders().First(f => f.Name.Equals(args[2], StringComparison.OrdinalIgnoreCase));
    var messages = e.GetMessages(folder).OrderByDescending(m => m.Received).Take(int.Parse(args[3])).ToList();
    using var client = OpenOutlook.Desktop.SafeRemoteImageLoader.CreateClient();
    var reasons = new Dictionary<string, int>();
    int total = 0, ok = 0;
    var formats = new Dictionary<string, int>();
    foreach (var m in messages)
    {
        var html = e.OpenMessage(m).BodyHtml;
        if (string.IsNullOrEmpty(html)) continue;
        var urls = System.Text.RegularExpressions.Regex.Matches(html, "<img[^>]*?\\ssrc\\s*=\\s*[\"']([^\"']+)[\"']", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(x => System.Net.WebUtility.HtmlDecode(x.Groups[1].Value)).Where(u => u.StartsWith("http", StringComparison.OrdinalIgnoreCase) || u.StartsWith("//")).Distinct().Take(25).ToList();
        int mok = 0;
        var tasks = urls.Select(async u =>
        {
            try { var b = await OpenOutlook.Desktop.SafeRemoteImageLoader.FetchAsync(u.StartsWith("//") ? "https:" + u : u, client); return ("OK", OpenOutlook.Desktop.SafeInlineImage.MimeType(b)); }
            catch (Exception ex) { return ("FAIL", ex.GetType().Name + ": " + ex.Message); }
        }).ToList();
        foreach (var (kind, detail) in await Task.WhenAll(tasks))
        {
            total++;
            if (kind == "OK") { ok++; mok++; formats[detail] = formats.GetValueOrDefault(detail) + 1; }
            else reasons[detail] = reasons.GetValueOrDefault(detail) + 1;
        }
        Console.WriteLine($"{mok,3}/{urls.Count,-3} {m.From,-28} {(m.Subject.Length > 60 ? m.Subject[..60] : m.Subject)}");
    }
    Console.WriteLine($"TOTAL {ok}/{total} loaded; formats: {string.Join(", ", formats.Select(kv => kv.Key + "=" + kv.Value))}");
    foreach (var kv in reasons.OrderByDescending(kv => kv.Value)) Console.WriteLine($"  {kv.Value,4}  {kv.Key}");
    return 0;
}

if (step == "imgprobe")
{
    // finds a message by subject in a (copy of a) PST, writes its HTML next to the file and tries every <img> the way the reader does: MailSmoke imgprobe <file> <subject part>
    using var e = OpenOutlook.PstNative.PstEngineFactory.Open(Path.GetFullPath(args[1]));
    var hit = e.AllFolders().SelectMany(f => e.GetMessages(f)).FirstOrDefault(m => m.Subject.Contains(args[2], StringComparison.OrdinalIgnoreCase));
    if (hit is null) { Console.WriteLine("no such message"); return 1; }
    var msg = e.OpenMessage(hit);
    var html = msg.BodyHtml;
    File.WriteAllText(Path.ChangeExtension(args[1], ".html"), html);
    Console.WriteLine($"{hit.Subject} | html {html.Length} chars | attachments {msg.Attachments.Count}");
    var urls = SafeHtmlDocument.FindImages(html).Where(x => x.ContactsExternalSite).Select(x => x.Value).ToList();     // exactly what the reader loads (img, background=, CSS url())
    var bg = System.Text.RegularExpressions.Regex.Matches(html, "(?:background(?:-image)?\\s*:[^;\"]*url\\(|background\\s*=\\s*[\"'])\\s*['\"]?([^'\")]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;
    Console.WriteLine($"{urls.Count} img sources, {bg} CSS/attribute background images");
    using var client = OpenOutlook.Desktop.SafeRemoteImageLoader.CreateClient();
    foreach (var u in urls)
    {
        string verdict;
        var fetchWatch = System.Diagnostics.Stopwatch.StartNew();
        try { var bytes = await OpenOutlook.Desktop.SafeRemoteImageLoader.FetchAsync(u, client); verdict = "OK " + bytes.Length; }
        catch (Exception ex) { verdict = "FAIL " + ex.GetType().Name + ": " + ex.Message; }
        verdict = $"{fetchWatch.ElapsedMilliseconds,6} ms " + verdict;
        // what the server really sends, without the reader's restrictions
        string raw;
        try
        {
            using var plain = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(10) };
            using var r = await plain.GetAsync(u, HttpCompletionOption.ResponseHeadersRead);
            var head = new byte[16];
            await using (var st = await r.Content.ReadAsStreamAsync()) { var n = await st.ReadAsync(head); head = head[..n]; }
            raw = $"{(int)r.StatusCode} {r.Content.Headers.ContentType} final={(r.RequestMessage!.RequestUri!.Host)} magic={Convert.ToHexString(head[..Math.Min(4, head.Length)])}";
        }
        catch (Exception ex) { raw = "plain fetch failed: " + ex.Message; }
        Console.WriteLine($"{verdict,-70} | {raw} | {(u.Length > 90 ? u[..90] + "…" : u)}");
    }
    return 0;
}

if (step == "selprobe")
{
    // Offline. Times the snapshot reader's character measurement on real mail: selprobe <copy of a pst> <subject text> [more subjects...]
    if (args.Length == 2 && File.Exists(args[1]) && args[1].EndsWith(".html", StringComparison.OrdinalIgnoreCase))
    {
        var file = SafeHtmlDocument.Build(File.ReadAllText(args[1]), new Dictionary<string, byte[]>());
        var fileWatch = System.Diagnostics.Stopwatch.StartNew();
        var fr = await BrowserHtmlRenderer.RenderDocumentAsync(file, 800, CancellationToken.None, trustedOriginal: false);
        Console.WriteLine($"[{Path.GetFileName(args[1])}] {fileWatch.ElapsedMilliseconds} ms, {fr.Pages.Count} tiles, {fr.Text.Count} runs, {new SnapshotText(fr.Text).Text.Length} chars");
        return 0;
    }
    if (args.Length < 3) { Console.Error.WriteLine("usage: selprobe <copy of a pst> <subject text>... | selprobe <file.html>"); return 2; }
    using var e = OpenOutlook.PstNative.PstEngineFactory.Open(args[1]);
    var all = e.AllFolders().SelectMany(f => e.GetMessages(f)).ToList();
    foreach (var wanted in args.Skip(2))
    {
        var hit = all.FirstOrDefault(m => m.Subject.Contains(wanted, StringComparison.OrdinalIgnoreCase));
        if (hit is null) { Console.WriteLine($"[{wanted}] no such message"); continue; }
        var msg = e.OpenMessage(hit);
        if (msg.BodyHtml is not { } html) { Console.WriteLine($"[{wanted}] no HTML body"); continue; }
        var doc = SafeHtmlDocument.Build(html, new Dictionary<string, byte[]>());
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var r = await BrowserHtmlRenderer.RenderDocumentAsync(doc, 800, CancellationToken.None, trustedOriginal: false);
            var text = new SnapshotText(r.Text);
            Console.WriteLine($"[{hit.Subject}] {watch.ElapsedMilliseconds} ms, {r.Pages.Count} tiles, {r.Text.Count} runs, {text.Text.Length} chars");
            Console.WriteLine("  " + text.Text[..Math.Min(600, text.Text.Length)].Replace("\n", " | ").Replace("\t", " <tab> "));
        }
        catch (Exception ex) { Console.WriteLine($"[{wanted}] FAILED after {watch.ElapsedMilliseconds} ms: {ex.Message}"); }
    }
    return 0;
}
if (step == "openeditable")
{
    // the way the application opens a data file (editable when it can be, else read-only with a reason); no accounts needed: MailSmoke openeditable <file>
    var file = Path.GetFullPath(args[1]);
    var sw0 = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        using var opened = OpenOutlook.PstNative.PstEngineFactory.OpenEditable(file, out var reason);
        Console.WriteLine($"opened {file} in {sw0.ElapsedMilliseconds} ms: {opened.AllFolders().Count()} folders, writable={opened.CanWrite}{(reason is null ? "" : ", read-only because: " + reason)}");
    }
    catch (Exception e) { Console.WriteLine("open failed: " + e.GetType().Name + ": " + e.Message); }
    return 0;
}

// Everything below signs in to the owner's real accounts, and the end of the file SENDS MAIL between them. It runs only for a step named
// explicitly ("sendtest" for the send / reply / forward test). An unknown or missing step used to fall through to that test: a typo, or an
// old build that did not have a new step yet, sent real messages (it happened four times on 2026-10-15).
var liveSteps = new HashSet<string>(StringComparer.Ordinal) { "sendtest", "rendergmail", "renderlive", "livegmail", "livepush", "scanlocal", "launchtest", "render", "localfolder", "mirror", "gmailtiming", "openpath", "gmailsync", "timing", "junkscan", "attach" };
if (!liveSteps.Contains(step))
{
    Console.Error.WriteLine(step.Length == 0 ? "MailSmoke: name a step." : $"MailSmoke: unknown step '{step}'.");
    Console.Error.WriteLine("Offline steps: mkbigrecips, mkmulti, mkmany, touchall, wvprobe, compare, imgsurvey, imgprobe, openeditable, selprobe, colorprobe.");
    Console.Error.WriteLine("Live steps (use the real accounts): " + string.Join(", ", liveSteps.Order()) + ". 'sendtest' sends real mail.");
    return 2;
}

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
        if (args.Length > 1 && args[1] == "cache") GraphAccountVerification.CacheEnabled = true;
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
if (step == "localfolder" && mAcc is not null)
{
    // times reading folders from the real mailbox copy (opened read-only; close OpenOutlook first)
    var path = OpenOutlook.Mirror.MirrorLocations.PstPathFor(new OpenOutlook.Mirror.MirrorSettingsStore().Load(), mAcc.AccountId, mAcc.DisplayAddress);
    Console.WriteLine(path + (File.Exists(path) ? $" ({new FileInfo(path).Length / 1048576} MB)" : " (missing)"));
    var swo = System.Diagnostics.Stopwatch.StartNew();
    using var pst = OpenOutlook.PstNative.PstEngineFactory.Open(path, false);
    Console.WriteLine($"open: {swo.ElapsedMilliseconds} ms");
    using var st = new OpenOutlook.Mirror.SyncStateStore(Path.ChangeExtension(path, ".sync"));
    foreach (var id in new[] { "inbox", "inbox", "junkemail" })
    {
        swo.Restart();
        var folderId = id == "junkemail" ? st.Folders().FirstOrDefault(f => pst.FindFolder(f.PstNid)?.Name == "Junk Email")?.RemoteId ?? id : id;
        var page = OpenOutlook.Mirror.LocalMailboxReader.Read(pst, st, folderId);
        Console.WriteLine($"{id}: {swo.ElapsedMilliseconds} ms, {(page is null ? "no local answer" : page.TotalCount + " messages, " + page.UnreadCount + " unread, showing " + page.Messages.Count)}");
        foreach (var m in page?.Messages.Take(3) ?? [])
        {
            swo.Restart();
            var body = OpenOutlook.Mirror.LocalMailboxReader.ReadBody(pst, st, m.Id);
            Console.WriteLine($"   body of \"{(m.Subject.Length > 40 ? m.Subject[..40] : m.Subject)}\": {swo.ElapsedMilliseconds} ms, {(body is null ? "not in the copy" : (body.Html is null ? "text " : "html ") + (body.Html ?? body.Text).Length + " chars")}");
        }
    }
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
if (step == "gmailtiming" && gBox is not null)
{
    // times single Gmail calls (stars then un-stars one inbox message; nothing is deleted)
    var swg = System.Diagnostics.Stopwatch.StartNew();
    async Task<T> TimeG<T>(string label, Func<Task<T>> f) { swg.Restart(); var r = await f(); Console.WriteLine($"{label}: {swg.ElapsedMilliseconds} ms"); return r; }
    var ids = await TimeG("list inbox ids (includes token check)", () => gBox.ListLabelMessageIdsAsync("INBOX", 5));
    await TimeG("list inbox ids again", () => gBox.ListLabelMessageIdsAsync("INBOX", 5));
    var one = new[] { ids[0] };
    await TimeG("star (modify)", async () => { await gBox.SetStarredAsync(one, true); return 0; });
    await TimeG("unstar (modify)", async () => { await gBox.SetStarredAsync(one, false); return 0; });
    await TimeG("mark read (modify)", async () => { await gBox.SetReadAsync(one, true); return 0; });
    var labels = await TimeG("labels with counts (the refresh after every action)", () => gBox.ListLabelsAsync());
    Console.WriteLine($"{labels.Count} labels");
    return 0;
}

if (step == "openpath")
{
    // opens one PST read-only from any path (a mapped drive or a UNC share) and counts its folders: MailSmoke openpath <file>
    var target = args[1];
    var swp = System.Diagnostics.Stopwatch.StartNew();
    Console.WriteLine($"path: {target}; exists={File.Exists(target)}; full path={Path.GetFullPath(target)}");
    try
    {
        using var pst = OpenOutlook.PstNative.PstEngineFactory.Open(target, false);
        Console.WriteLine($"opened in {swp.ElapsedMilliseconds} ms: {pst.AllFolders().Count()} folders, writable={pst.CanWrite}");
    }
    catch (Exception e) { Console.WriteLine("open failed: " + e.GetType().Name + ": " + e.Message); }
    return 0;
}

if (step == "gmailsync" && gBox is not null)
{
    // times a normal synchronisation pass of the real Gmail mailbox copy (the same work the app does after a change); close OpenOutlook first
    var set = new OpenOutlook.Mirror.MirrorSettingsStore().Load();
    var opt = set.For(gAcc!.AccountId);
    var path = OpenOutlook.Mirror.MirrorLocations.PstPathFor(set, gAcc.AccountId, gAcc.DisplayAddress);
    using var pst = OpenOutlook.PstNative.PstEngineFactory.Open(path, true);
    using var st = new OpenOutlook.Mirror.SyncStateStore(Path.ChangeExtension(path, ".sync"));
    var src = new OpenOutlook.Mirror.GmailMirrorSource(gBox, gAcc.CanModifyGmail);
    for (var pass = 1; pass <= 2; pass++)
    {
        var swm = System.Diagnostics.Stopwatch.StartNew();
        var res = await OpenOutlook.Mirror.MirrorSyncEngine.SyncAsync(src, pst, st, new OpenOutlook.Mirror.MirrorSyncOptions(opt.KeepMonths, opt.MaxAttachmentBytes), null, CancellationToken.None);
        Console.WriteLine($"sync pass {pass}: {swm.Elapsed.TotalSeconds:0.0} s, {res}");
    }
    return 0;
}

if (step == "timing" && mAcc is not null)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    async Task<T> Time<T>(string label, Func<Task<T>> f) { sw.Restart(); var r = await f(); Console.WriteLine($"{label}: {sw.ElapsedMilliseconds} ms"); return r; }
    var tok = await Time("token (first)", () => mSession!.GetAccessTokenAsync());
    await Time("token (cached)", () => mSession!.GetAccessTokenAsync());
    var rd = new GraphInboxReader(graphHttp, mAcc.AccountId);
    var p1 = await Time("inbox page (first call, includes connection setup)", () => rd.GetInboxAsync(tok));
    var p2 = await Time("inbox page (warm connection)", () => rd.GetInboxAsync(tok));
    await Time("inbox page again", () => rd.GetInboxAsync(tok));
    var m = p2.Messages.First();
    await Time("message body", async () => await rd.GetMessageBodyAsync(tok, m.Id));
    await Time("message body again", async () => await rd.GetMessageBodyAsync(tok, m.Id));
    await Time("folder tree", () => new GraphMailFolderReader(graphHttp, mAcc.AccountId).GetFoldersAsync(tok));
    var wr = new GraphMailWriter(graphHttp, mAcc.AccountId);
    await Time("mark read (verify + patch)", async () => { await wr.SetReadAsync(tok, m.Id, m.IsRead); return 0; });
    Console.WriteLine($"{p2.Messages.Count} messages per page");
    return 0;
}

if (step == "junkscan" && mAcc is not null)
{
    // dry run: lists what the Junk Cleaner would remove from the Hotmail Junk folder; nothing is moved or saved
    var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OutlookJunkCleaner", "config.json");
    var cfg = File.Exists(legacy)
        ? OpenOutlook.JunkCleaner.JunkCleanerSettingsStore.PreviewLegacyConfig(legacy).ImportForAccount(mAcc.AccountId) with { Enabled = true }
        : new OpenOutlook.JunkCleaner.JunkCleanerAccountSettings { AccountId = mAcc.AccountId, Enabled = true, Keywords = ["temu"] };
    Console.WriteLine($"settings from {(File.Exists(legacy) ? legacy : "built-in sample")}: {cfg.Keywords.Count} keywords, rules high={cfg.Rules.DeleteHighImportance} noTo={cfg.Rules.DeleteMissingTo} onBehalf={cfg.Rules.DeleteOnBehalfOf}");
    var runner = new MicrosoftJunkCleanerRunner(new GraphJunkMailReader(graphHttp, async ct => await mSession!.GetAccessTokenAsync(ct), mAcc.AccountId),
        new GraphMailWriter(graphHttp, mAcc.AccountId), ct => mSession!.GetAccessTokenAsync(ct), mAcc.AccountId);
    var scan = await runner.ScanAsync(cfg);
    Console.WriteLine($"Junk folder: {scan.Scanned} messages, {scan.Matched.Count} would be moved to Deleted Items");
    foreach (var h in scan.Matched.Take(40)) Console.WriteLine($"  {h.Sender}  |  {h.Subject}  [{string.Join(", ", h.Reasons)}]");
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

static class ColorProbe
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] public static extern uint GetPixel(IntPtr dc, int x, int y);
}
