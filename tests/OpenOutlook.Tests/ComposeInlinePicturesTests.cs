using System.Net;
using System.Text;
using System.Text.Json;
using OpenOutlook.Desktop;
using OpenOutlook.Providers.Google;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Tests;

/// <summary>Pictures in an HTML message travel as inline (cid:) parts, not data: URIs (Gmail does not show those); owner 2026-10-18.</summary>
public sealed class ComposeInlinePicturesTests
{
    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
    private static readonly byte[] Gif = "GIF89a"u8.ToArray().Concat(new byte[] { 1, 0, 1, 0 }).ToArray();
    private static string Uri(string mime, byte[] data) => ComposeInlinePictures.ToDataUri(mime, data);

    [Fact]
    public void Data_pictures_become_cid_parts_once_each_and_come_back_for_the_editor()
    {
        var html = $"<div>Hi <img src=\"{Uri("image/png", Png)}\"> and again <img src=\"{Uri("image/png", Png)}\"></div><img src=\"{Uri("image/gif", Gif)}\"><img src=\"https://example.test/a.png\">";
        var (sent, pictures) = ComposeInlinePictures.Extract(html);
        Assert.Equal(2, pictures.Count);                                              // the same picture twice is one part
        Assert.DoesNotContain("data:", sent);
        Assert.Contains("https://example.test/a.png", sent);                          // web pictures stay links
        var png = pictures[0];
        Assert.Matches("^image-[0-9a-f]{16}\\.png$", png.FileName);
        Assert.Equal(png.FileName + "@openoutlook", png.ContentId);
        Assert.Equal(2, sent.Split("cid:" + png.ContentId).Length - 1);
        Assert.Equal("image/gif", pictures[1].MimeType);
        Assert.Equal(Gif, pictures[1].Data);
        // the name and id come from the bytes: the same picture keeps them on every save
        Assert.Equal(png.ContentId, ComposeInlinePictures.Extract($"<img src=\"{Uri("image/png", Png)}\">").Pictures[0].ContentId);
        Assert.True(ComposeInlinePictures.IsOwnName(png.FileName));
        Assert.False(ComposeInlinePictures.IsOwnName("image001.png"));

        var back = ComposeInlinePictures.Restore(sent, pictures);
        Assert.DoesNotContain("cid:", back);
        Assert.Equal(2, back.Split(Uri("image/png", Png)).Length - 1);
        Assert.Contains("cid:other@x", ComposeInlinePictures.Restore("<img src=\"cid:other@x\">", pictures));     // unknown parts are left alone
    }

    [Fact]
    public void Only_real_pictures_of_a_sane_size_are_sent()
    {
        // declared png, but the bytes are not a picture: the data: text is not sent at all
        var (sent, pictures) = ComposeInlinePictures.Extract($"<img src=\"{Uri("image/png", "<svg onload=x>"u8.ToArray())}\">");
        Assert.Empty(pictures);
        Assert.DoesNotContain("data:", sent);
        Assert.Null(ComposeInlinePictures.FromDataUri("data:image/svg+xml;base64,PHN2Zz4="));
        Assert.Null(ComposeInlinePictures.FromFile(new byte[] { 1, 2, 3 }));
        Assert.Null(ComposeInlinePictures.FromFile(Png.Concat(new byte[ComposeInlinePictures.MaxPictureBytes]).ToArray()));
        Assert.Equal("image/png", ComposeInlinePictures.FromFile(Png)!.MimeType);
        Assert.Equal("<div>no pictures</div>", ComposeInlinePictures.Extract("<div>no pictures</div>").Html);     // untouched when there is nothing to do
    }

    [Fact]
    public void Gmail_sends_the_html_and_its_pictures_in_a_related_part()
    {
        var m = new GmailOutgoing { From = "me@gmail.test", To = "you@example.test", Subject = "Pictures", Html = "<div><img src=\"cid:image-1@openoutlook\"></div>", Text = "" };
        m.InlineImages.Add(new GmailInlineImage("image-1@openoutlook", "image-1.png", "image/png", Png));
        m.Attachments.Add(new GmailOutgoingAttachment("notes.txt", "text/plain", "hi"u8.ToArray()));
        var mime = Encoding.ASCII.GetString(GmailMimeBuilder.Build(m));
        var mixed = mime.IndexOf("multipart/mixed", StringComparison.Ordinal);
        var related = mime.IndexOf("multipart/related", StringComparison.Ordinal);
        var alternative = mime.IndexOf("multipart/alternative", StringComparison.Ordinal);
        Assert.True(mixed >= 0 && related > mixed && alternative > related, mime[..Math.Min(600, mime.Length)]);
        Assert.Contains("Content-ID: <image-1@openoutlook>\r\n", mime);
        Assert.Contains("Content-Disposition: inline; filename=\"image-1.png\"", mime);
        Assert.Contains(Convert.ToBase64String(Png), mime);
        Assert.True(mime.IndexOf("Content-ID", StringComparison.Ordinal) < mime.IndexOf("notes.txt", StringComparison.Ordinal));   // the picture is in the related part, the file after it

        m.InlineImages[0] = new GmailInlineImage("bad>\r\nBcc: x@evil.test", "a.png", "image/png", Png);
        Assert.Throws<ArgumentException>(() => GmailMimeBuilder.Build(m));        // no header injection through a Content-ID
    }

    [Fact]
    public async Task Gmail_backend_turns_data_pictures_into_inline_parts()
    {
        var html = $"<div>Look <img src=\"{Uri("image/png", Png)}\"></div>";
        var build = typeof(GmailComposeBackend).GetMethod("Build", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var backend = new GmailComposeBackend("me@gmail.test", null!);
        var mime = Encoding.ASCII.GetString((byte[])build.Invoke(backend, [new ComposeDraft("you@example.test", "", "", "Hi", html, true), Array.Empty<ComposeFile>()])!);
        Assert.Contains("multipart/related", mime);
        Assert.DoesNotContain("data:image", DecodeHtmlPart(mime));
        Assert.Contains("cid:image-", DecodeHtmlPart(mime));
        await Task.CompletedTask;
    }

    private static string DecodeHtmlPart(string mime)
    {
        var start = mime.IndexOf("text/html; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n", StringComparison.Ordinal);
        var body = mime[(mime.IndexOf("\r\n\r\n", start, StringComparison.Ordinal) + 4)..];
        body = body[..body.IndexOf("--", StringComparison.Ordinal)];
        return Encoding.UTF8.GetString(Convert.FromBase64String(body.Replace("\r\n", "")));
    }

    [Fact]
    public async Task Microsoft_draft_gets_new_pictures_only_removes_dropped_ones_and_reopens_with_them()
    {
        var calls = new List<(string Method, string Path, string? Body)>();
        var stored = new List<(string Id, string Name, bool Inline)> { ("old-pic", "image-00000000000000aa.png", true), ("file", "notes.txt", false) };
        string? savedBody = null;
        var png = ComposeInlinePictures.FromFile(Png)!;
        using var http = new HttpClient(new Handler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            calls.Add((request.Method.Method, path + request.RequestUri.Query, body));
            HttpResponseMessage response;
            if (path == "/v1.0/me") response = Json(HttpStatusCode.OK, """{"id":"owner"}""");
            else if (path == "/v1.0/me/messages/draft" && request.Method == HttpMethod.Patch)
            {
                savedBody = JsonDocument.Parse(body!).RootElement.GetProperty("body").GetProperty("content").GetString();
                response = Json(HttpStatusCode.OK, """{"id":"draft"}""");
            }
            else if (path == "/v1.0/me/messages/draft" && request.Method == HttpMethod.Get)
                response = Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { id = "draft", isDraft = true, subject = "S", body = new { contentType = "html", content = savedBody }, hasAttachments = true }));
            else if (path == "/v1.0/me/messages/draft/attachments" && request.Method == HttpMethod.Get)
                response = Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { value = stored.Select(s => new { id = s.Id, name = s.Name, size = 10, isInline = s.Inline }) }));
            else if (path == "/v1.0/me/messages/draft/attachments" && request.Method == HttpMethod.Post)
            {
                var json = JsonDocument.Parse(body!).RootElement;
                stored.Add(("new-pic", json.GetProperty("name").GetString()!, json.GetProperty("isInline").GetBoolean()));
                response = new HttpResponseMessage(HttpStatusCode.Created);
            }
            else if (path == "/v1.0/me/messages/draft/attachments/old-pic" && request.Method == HttpMethod.Delete)
            {
                stored.RemoveAll(s => s.Id == "old-pic");
                response = new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            else if (path == "/v1.0/me/messages/draft/attachments/new-pic" && request.Method == HttpMethod.Get)
                response = Json(HttpStatusCode.OK, JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["@odata.type"] = "#microsoft.graph.fileAttachment", ["id"] = "new-pic", ["name"] = png.FileName, ["contentType"] = "image/png",
                    ["contentId"] = png.ContentId, ["isInline"] = true, ["contentBytes"] = Convert.ToBase64String(Png)
                }));
            else throw new Exception("Unexpected Graph call " + request.Method + " " + path);
            response.RequestMessage = request;
            return response;
        }));
        var backend = new GraphComposeBackend("me@hotmail.test", new GraphMailWriter(http, "owner"), () => Task.FromResult("access"));
        var html = $"<div>Look <img src=\"{Uri("image/png", Png)}\"></div>";
        await backend.SaveAsync("draft", new ComposeDraft("you@example.test", "", "", "S", html, true), [], new Progress<string>());

        Assert.Contains("cid:" + png.ContentId, savedBody);
        Assert.DoesNotContain("data:", savedBody);
        var post = Assert.Single(calls, c => c.Method == "POST");
        var payload = JsonDocument.Parse(post.Body!).RootElement;
        Assert.Equal((png.FileName, png.ContentId, true, "image/png"),
            (payload.GetProperty("name").GetString(), payload.GetProperty("contentId").GetString(), payload.GetProperty("isInline").GetBoolean(), payload.GetProperty("contentType").GetString()));
        Assert.Contains(calls, c => c.Method == "DELETE");                           // our picture that left the body is removed
        Assert.Contains(stored, s => s.Name == "notes.txt");                          // files are never touched

        // saving again uploads nothing new
        calls.Clear();
        await backend.SaveAsync("draft", new ComposeDraft("you@example.test", "", "", "S", html, true), [], new Progress<string>());
        Assert.DoesNotContain(calls, c => c.Method is "POST" or "DELETE");

        // reopening: the picture is back in the body, the attachment bar lists only the file
        var loaded = await backend.LoadAsync("draft");
        Assert.Contains(Uri("image/png", Png), loaded.Draft.Body);
        Assert.Equal("notes.txt", Assert.Single(loaded.Files).Name);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body) };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
