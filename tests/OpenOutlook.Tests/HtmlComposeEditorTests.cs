using OpenOutlook.Desktop;
using PuppeteerSharp;

namespace OpenOutlook.Tests;

/// <summary>The compose body's HTML editor (owner 2026-10-18): the wrapper that carries the default font, and the editor page's commands run in the
/// bundled Chromium (the engine of WebView2), checked by the HTML they produce.</summary>
public sealed class HtmlComposeEditorTests
{
    [Fact]
    public void The_sent_body_carries_the_default_font_and_reopens_without_nesting()
    {
        var wrapped = HtmlEditorDocument.Wrap("<div>Hello <b>you</b></div><div>Second</div>");
        Assert.StartsWith("<div class=\"oo-compose\" style=\"font-family:Aptos,", wrapped);
        Assert.Contains("font-size:12pt", wrapped);
        Assert.Equal("<div>Hello <b>you</b></div><div>Second</div>", HtmlEditorDocument.Unwrap(wrapped));
        Assert.Equal("<div>a</div>", HtmlEditorDocument.Unwrap(HtmlEditorDocument.Unwrap(HtmlEditorDocument.Wrap("<div>a</div>"))));
        // a body that only starts with the wrapper (a reply quoted after it) is left alone
        var two = HtmlEditorDocument.Wrap("x") + "<div>quoted</div>";
        Assert.Equal(two, HtmlEditorDocument.Unwrap(two));
        Assert.Equal("", HtmlEditorDocument.Wrap("<div><br></div>"));                             // an empty editor sends nothing
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("<div><br></div>", true)]
    [InlineData("<div>&nbsp;</div><p> </p>", true)]
    [InlineData("<div>a</div>", false)]
    [InlineData("<div><img src=\"https://x.test/a.png\"></div>", false)]
    [InlineData("<table><tr><td></td></tr></table>", false)]
    public void Blank_bodies_are_recognised(string html, bool blank) => Assert.Equal(blank, HtmlEditorDocument.IsBlank(html));

    [Fact]
    public void Plain_text_becomes_one_div_per_line_and_script_results_are_decoded()
    {
        Assert.Equal("<div>a &lt;b&gt;</div><div><br></div><div>c</div>", HtmlEditorDocument.FromPlainText("a <b>\r\n\r\nc"));
        Assert.Equal("<b>\"x\"</b>", HtmlEditorDocument.ScriptString("\"<b>\\\"x\\\"</b>\""));             // WebView2: JSON
        Assert.Equal("<b>x</b>", HtmlEditorDocument.ScriptString("<b>x</b>"));                         // WebKitGTK: the bare text
        Assert.Null(HtmlEditorDocument.ScriptString("null"));
        // the command and its value are JSON: a quote cannot end the script string
        Assert.Equal("window.oo&&window.oo.run(\"link\",\"https://a.test/\\u0027);alert(1)//\")", HtmlEditorDocument.Call("link", "https://a.test/');alert(1)//"));
        var state = HtmlEditorDocument.ParseState("{\"b\":true,\"u\":false,\"al\":\"center\",\"font\":\"Georgia\",\"size\":14,\"ul\":true}")!;
        Assert.True(state.Bold && state.Bullets && !state.Underline);
        Assert.Equal(("center", "Georgia", 14d), (state.Align, state.Font, state.Size));
    }

    private static async Task<(IBrowser Browser, IPage Page)?> OpenEditorAsync(string body)
    {
        if (BrowserHtmlRenderer.FindBrowser() is not { } exe) return null;
        var browser = await BrowserProcessTracker.LaunchAsync(exe);
        var page = await browser.NewPageAsync();
        await page.SetContentAsync(HtmlEditorDocument.Shell);
        await page.EvaluateExpressionAsync(HtmlEditorDocument.SetCall(body));
        return (browser, page);
    }

    /// <summary>Selects the text <paramref name="text"/> inside the editor, as a mouse drag would.</summary>
    private static Task Select(IPage page, string text) => page.EvaluateExpressionAsync(
        "(function(t){const ed=document.getElementById('editor');ed.focus();const w=document.createTreeWalker(ed,NodeFilter.SHOW_TEXT);" +
        "while(w.nextNode()){const n=w.currentNode,i=n.data.indexOf(t);if(i>=0){const r=document.createRange();r.setStart(n,i);r.setEnd(n,i+t.length);" +
        "const s=getSelection();s.removeAllRanges();s.addRange(r);return true;}}return false;})(" + System.Text.Json.JsonSerializer.Serialize(text) + ")");

    private static async Task<string> Run(IPage page, string command, string? value = null)
    {
        await Task.Delay(30);                                                        // selectionchange is delivered asynchronously
        await page.EvaluateExpressionAsync(HtmlEditorDocument.Call(command, value));
        return await page.EvaluateExpressionAsync<string>("window.oo.get()");
    }

    [Fact]
    public async Task Font_commands_produce_html_that_mail_programs_show()
    {
        if (await OpenEditorAsync("<div>one two three four</div>") is not var (browser, page)) return;
        await using var _ = browser;
        await Select(page, "two");
        Assert.Contains("<b>two</b>", (await Run(page, "bold")).Replace("<span style=\"font-weight: bold;\">two</span>", "<b>two</b>"));
        await Select(page, "three");
        var html = await Run(page, "fontSize", "18");
        Assert.Contains("font-size: 18pt", html);
        Assert.DoesNotContain("xxx-large", html);                                   // the browser's marker size is replaced by the points chosen
        await Select(page, "three");
        Assert.Contains("font-size: 20pt", await Run(page, "grow"));               // 18 -> 20, Outlook's next size
        await Select(page, "four");
        html = await Run(page, "foreColor", "#C00000");
        Assert.Matches("color: (#c00000|rgb\\(192, 0, 0\\))", html);
        await Select(page, "one");
        Assert.Contains("background-color", await Run(page, "hiliteColor", "#FFFF00"));
        await Select(page, "one");
        Assert.Contains("Georgia", await Run(page, "fontName", "Georgia"));
        await Select(page, "one two");
        html = await Run(page, "clear");
        Assert.DoesNotContain("Georgia", html);
        Assert.DoesNotContain("background-color", html);

        var state = HtmlEditorDocument.ParseState(await page.EvaluateExpressionAsync<string>("JSON.stringify(window.oo.state())"))!;
        Assert.Equal(12, state.Size);                                               // after Clear Formatting the cursor is in the 12 pt default again
    }

    [Fact]
    public async Task Paragraph_commands_lists_case_links_and_tables()
    {
        if (await OpenEditorAsync("<div>first line</div><div>second line</div>") is not var (browser, page)) return;
        await using var _ = browser;
        await Select(page, "first");
        Assert.Contains("<ul><li>first line</li></ul>", await Run(page, "bullets"));
        await Select(page, "first");
        Assert.Contains("list-style-type: square", await Run(page, "bullets", "square"));
        await Select(page, "first");
        Assert.DoesNotContain("<ul", await Run(page, "bullets", "none"));          // None ends the list
        await Select(page, "second");
        Assert.Contains("<ol>", await Run(page, "numbering"));
        await Select(page, "second");
        Assert.Contains("text-align: center", await Run(page, "justifyCenter"));
        await Select(page, "first");
        Assert.Contains("line-height: 1.5", await Run(page, "lineSpacing", "1.5"));
        await Select(page, "first line");
        Assert.Contains("First Line", await Run(page, "changeCase", "capitalize"));
        await Select(page, "First Line");
        Assert.Contains("FIRST LINE", await Run(page, "changeCase", "upper"));
        await Select(page, "FIRST LINE");
        var html = await Run(page, "style", "h1");
        Assert.Contains("<h1 style=\"font-size:20pt", html.Replace("; ", ";"));
        await Select(page, "second");
        Assert.Contains("<a href=\"https://example.test/\">second</a>", await Run(page, "link", "https://example.test/"));
        await Select(page, "second");
        Assert.DoesNotContain("javascript", await Run(page, "link", "javascript:alert(1)\nclick"));     // only http, https and mailto become links
        Assert.Matches("<table[^>]*><tbody><tr><td[^>]*>&nbsp;</td><td", await Run(page, "table", "2,2"));
    }

    [Fact]
    public async Task The_page_reports_the_formatting_at_the_cursor()
    {
        if (await OpenEditorAsync("<div><b>bold</b> <span style=\"font-family:Georgia;font-size:14pt\">big</span></div>") is not var (browser, page)) return;
        await using var _ = browser;
        await Select(page, "bold");
        await Task.Delay(30);
        var state = HtmlEditorDocument.ParseState(await page.EvaluateExpressionAsync<string>("JSON.stringify(window.oo.state())"))!;
        Assert.True(state.Bold);
        Assert.Equal("Aptos", state.Font);
        await Select(page, "big");
        await Task.Delay(30);
        state = HtmlEditorDocument.ParseState(await page.EvaluateExpressionAsync<string>("JSON.stringify(window.oo.state())"))!;
        Assert.False(state.Bold);
        Assert.Equal(("Georgia", 14d), (state.Font, state.Size));
    }
}
