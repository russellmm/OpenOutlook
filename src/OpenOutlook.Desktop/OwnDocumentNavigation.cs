namespace OpenOutlook.Desktop;

/// <summary>
/// Lets a web view load the document the app itself handed it with NavigateToString, and nothing else. WebView2 (Avalonia.Controls.WebView 11.4)
/// loads such a document as a <c>data:text/html;base64,...</c> URL; the earlier guards allowed only <c>about:</c> and so cancelled the app's own
/// page. The reader then fell back to the snapshot (block-only text selection) for most messages, and the compose editor could not load
/// (found 2026-10-15). A data: document is accepted only while one is expected, once per NavigateToString; links, scripts and redirects that try to
/// navigate to data: or anything else are still refused.
/// </summary>
public sealed class OwnDocumentNavigation
{
    private int _expected;
    private string? _expectedFile;

    /// <summary>Call right before Navigate(file) for a document written to the private reader folder (too large for NavigateToString).</summary>
    public void ExpectFile(Uri file) => Volatile.Write(ref _expectedFile, file.AbsoluteUri);

    /// <summary>Call right before NavigateToString. One slot: a second call before the first document started does not allow two.</summary>
    public void Expect() => Interlocked.Exchange(ref _expected, 1);

    /// <summary>True when the navigation is the expected own document (or an about: page); consumes the expectation.</summary>
    public bool Allows(Uri? request)
    {
        if (request is null) return false;
        if (request.Scheme == "about") return true;
        if (request.Scheme == "file")                                          // only the exact file the app just wrote, once
            return request.AbsoluteUri == Volatile.Read(ref _expectedFile) && Interlocked.Exchange(ref _expectedFile, null) is not null;
        if (request.Scheme != "data" || !request.OriginalString.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase)) return false;
        while (true)
        {
            var current = Volatile.Read(ref _expected);
            if (current <= 0) return false;
            if (Interlocked.CompareExchange(ref _expected, current - 1, current) == current) return true;
        }
    }

    // Not cleared when a navigation completes: the previous page's late NavigationCompleted (rapid deletes) would otherwise cancel the
    // document that was just requested. Larger documents raise no NavigationStarted at all, so the slot can stay set until the next one.
}
