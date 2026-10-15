using OpenOutlook.Desktop;

namespace OpenOutlook.Tests;

public sealed class OwnDocumentNavigationTests
{
    private static readonly Uri Own = new("data:text/html;charset=utf-8;base64,PCFkb2N0eXBlIGh0bWw+");

    [Fact]
    public void Allows_the_requested_document_once_and_nothing_else()
    {
        var guard = new OwnDocumentNavigation();
        Assert.False(guard.Allows(Own));                                        // nothing was asked for: a data: page is refused
        guard.Expect();
        Assert.True(guard.Allows(Own));                                         // the document the app handed over
        Assert.False(guard.Allows(Own));                                        // a second data: navigation (a script, a link) is not
        guard.Expect();
        guard.Expect();                                                         // one slot, however often it is set
        Assert.True(guard.Allows(Own));
        Assert.False(guard.Allows(Own));
    }

    [Fact]
    public void Other_schemes_stay_blocked_and_about_pages_stay_allowed()
    {
        var guard = new OwnDocumentNavigation();
        guard.Expect();
        Assert.False(guard.Allows(new Uri("https://example.com/")));
        Assert.False(guard.Allows(new Uri("data:image/svg+xml;base64,PHN2Zz4=")));   // only an HTML document
        Assert.False(guard.Allows(new Uri("file:///C:/Windows/win.ini")));
        Assert.False(guard.Allows(null));
        Assert.True(guard.Allows(new Uri("about:blank")));
        Assert.True(guard.Allows(Own));                                         // the expectation was not used up by the refused ones
    }

    [Fact]
    public void A_large_message_file_is_allowed_only_for_the_exact_path_and_only_once()
    {
        var guard = new OwnDocumentNavigation();
        var file = new Uri(Path.Combine(Path.GetTempPath(), "openoutlook-reader-view", "message-1.html"));
        Assert.False(guard.Allows(file));                                       // not written by the app yet
        guard.ExpectFile(file);
        Assert.False(guard.Allows(new Uri(Path.Combine(Path.GetTempPath(), "other.html"))));
        Assert.True(guard.Allows(file));
        Assert.False(guard.Allows(file));                                       // a reload or a link back to it is refused
    }
}

