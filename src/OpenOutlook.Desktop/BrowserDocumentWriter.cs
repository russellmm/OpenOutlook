namespace OpenOutlook.Desktop;

/// <summary>
/// Writes a hardened message document to a private file so the system browser can open it.
///
/// This exists because the reading pane cannot offer text selection on every display server: the
/// embedded web view does not composite into an Avalonia window under Wayland, and the snapshot
/// reader is a bitmap. A browser has the HTML engine already, so opening the same sanitised document
/// there gives real selection, copying, find-in-page and printing.
///
/// The content is other people's mail, so the file goes in a directory only this user can read, is
/// written with owner-only permissions, and previous copies are removed -- without pruning, every
/// message read would leave another readable copy of somebody's correspondence on disk.
/// </summary>
public static class BrowserDocumentWriter
{
    /// <summary>Documents larger than this are not written; the reader has no use case for them.</summary>
    public const long MaximumDocumentCharacters = 64L * 1024 * 1024;

    private static string ScratchDirectory =>
        Path.Combine(Path.GetTempPath(), "openoutlook-reader");

    /// <summary>
    /// Writes the document and returns its path. Throws if the document is empty or absurdly large.
    /// </summary>
    public static string Write(string document, string? directory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(document);
        if (document.Length > MaximumDocumentCharacters)
            throw new InvalidDataException("This message is too large to open in a browser.");

        directory ??= ScratchDirectory;
        Directory.CreateDirectory(directory);
        TryRestrictDirectory(directory);
        PrunePreviousDocuments(directory);

        var path = Path.Combine(directory,
            $"message-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.html");
        File.WriteAllText(path, document, System.Text.Encoding.UTF8);
        TryRestrictFile(path);
        return path;
    }

    /// <summary>Removes message files left by earlier reads. Best effort: a stale file must not stop
    /// the user from opening the message they asked for.</summary>
    public static int PrunePreviousDocuments(string directory)
    {
        var removed = 0;
        try
        {
            foreach (var stale in Directory.EnumerateFiles(directory, "message-*.html"))
            {
                try { File.Delete(stale); removed++; }
                catch (IOException) { /* In use elsewhere, or already gone. */ }
                catch (UnauthorizedAccessException) { /* Not ours to delete. */ }
            }
        }
        catch (IOException) { /* Directory vanished between create and enumerate. */ }
        catch (UnauthorizedAccessException) { /* Another user's directory at the same path. */ }
        return removed;
    }

    private static void TryRestrictDirectory(string directory)
    {
        try { File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        catch (Exception) { /* Non-POSIX filesystem: permissions there are not modelled this way. */ }
    }

    private static void TryRestrictFile(string path)
    {
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (Exception) { /* As above. */ }
    }
}
