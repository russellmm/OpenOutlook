using OpenOutlook.Desktop;

namespace OpenOutlook.Tests;

public sealed class SafeHtmlPreviewTests
{
    [Fact]
    public void KeepsReadableFormattingWithoutActivatingMarkup()
    {
        var runs = SafeHtmlPreview.Parse("""
            <h2>Hello &amp; welcome</h2><p>Normal <strong>bold</strong> and <em>italic</em>.</p>
            <ul><li>First</li><li>Second</li></ul>
            <a href="https://example.test/track">Read more</a><img src="https://example.test/pixel" />
            <script>fetch('https://example.test/hidden')</script>
            <style>body{background:url(https://example.test/hidden)}</style>
            """);
        var text = string.Concat(runs.Select(run => run.Text));
        Assert.Contains("Hello & welcome", text);
        Assert.Contains("\n\n", text);
        Assert.Contains("• First", text);
        Assert.Contains("Read more", text);
        Assert.Contains("[Image blocked]", text);
        Assert.DoesNotContain("fetch", text);
        Assert.DoesNotContain("background", text);
        Assert.DoesNotContain("https://", text);
        Assert.Contains(runs, run => run.Bold && run.Text.Contains("bold"));
        Assert.Contains(runs, run => run.Italic && run.Text.Contains("italic"));
        Assert.Contains(runs, run => run.Scale > 1 && run.Text.Contains("Hello"));
    }

    [Fact]
    public void RejectsUnboundedInput()
    {
        Assert.Throws<InvalidDataException>(() => SafeHtmlPreview.Parse(new string('x', 512 * 1024 + 1)));
    }
}
