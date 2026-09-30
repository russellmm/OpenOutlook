using System.Text;
using PstCore;

namespace OpenOutlook.Desktop;

public sealed record PstFolderExportProgress(int FoldersVisited, int MessagesExported);
public sealed record PstFolderExportResult(int FoldersExported, int MessagesExported);
public sealed class PstFolderExportException(uint messageNid, string reason, Exception inner) :
    IOException($"Message {messageNid:X8} could not be exported: {reason}.", inner)
{
    public uint MessageNid { get; } = messageNid;
}

/// <summary>Exports a bounded PST folder tree into a new directory of EML files.</summary>
public static class PstFolderEmlExporter
{
    public const int MaximumFolders = 200;
    public const int MaximumMessages = 10_000;
    public const int MaximumDepth = 16;

    public static Task<PstFolderExportResult> ExportAsync(PstStore store, MailFolder folder,
        string destinationDirectory, IProgress<PstFolderExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (store.CanWrite) throw new InvalidOperationException("Folder export requires a read-only PST store.");
        return ExportAsync(folder, destinationDirectory, store.GetMessages, store.OpenMessage,
            (message, path, ct) => PstMessageEmlExporter.ExportAsync(store, message, path, ct),
            progress, cancellationToken);
    }

    /// <summary>Reader injection permits safe synthetic tests without a PST.</summary>
    public static async Task<PstFolderExportResult> ExportAsync(MailFolder folder,
        string destinationDirectory, Func<MailFolder, IReadOnlyList<MailSummary>> listMessages,
        Func<MailSummary, MailMessage> openMessage,
        Func<MailMessage, string, CancellationToken, Task> exportMessage,
        IProgress<PstFolderExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(listMessages);
        ArgumentNullException.ThrowIfNull(openMessage);
        ArgumentNullException.ThrowIfNull(exportMessage);
        if (string.IsNullOrWhiteSpace(destinationDirectory) || !Path.IsPathFullyQualified(destinationDirectory))
            throw new ArgumentException("Select an absolute output directory.", nameof(destinationDirectory));
        PstAttachmentExporter.ValidateSuggestedFileName(Path.GetFileName(destinationDirectory.TrimEnd(Path.DirectorySeparatorChar)));
        var destination = Path.GetFullPath(destinationDirectory);
        var parent = Path.GetDirectoryName(destination)!;
        EnsureSafeDirectory(parent);
        EnsureDestinationIsNew(destination);
        var folders = Plan(folder);
        cancellationToken.ThrowIfCancellationRequested();

        string? temporary = null;
        try
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var candidate = Path.Combine(parent, ".openoutlook-folder-export-" + Guid.NewGuid().ToString("N") + ".tmp");
                if (File.Exists(candidate) || Directory.Exists(candidate) || new DirectoryInfo(candidate).LinkTarget is not null)
                    continue;
                if (OperatingSystem.IsWindows()) Directory.CreateDirectory(candidate);
                else Directory.CreateDirectory(candidate, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                temporary = candidate;
                break;
            }
            if (temporary is null) throw new IOException("Could not create a folder export temporary directory.");
            var messagesExported = 0;
            var foldersVisited = 0;
            foreach (var (source, relative) in folders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var output = relative.Length == 0 ? temporary : Path.Combine(temporary, relative);
                if (relative.Length != 0)
                {
                    if (OperatingSystem.IsWindows()) Directory.CreateDirectory(output);
                    else Directory.CreateDirectory(output, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                var summaries = listMessages(source) ?? throw new InvalidDataException("PST returned no message list.");
                if (summaries.Count > MaximumMessages - messagesExported)
                    throw new InvalidDataException("Folder export exceeds the message limit.");
                var seenMessages = new HashSet<uint>();
                foreach (var summary in summaries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (summary is null || !seenMessages.Add(summary.Nid))
                        throw new InvalidDataException("PST folder has duplicate or invalid message IDs.");
                    MailMessage message;
                    try { message = openMessage(summary); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception ex) when (ex is PstException or InvalidDataException or IOException)
                    {
                        throw new PstFolderExportException(summary.Nid, "the PST message could not be read", ex);
                    }
                    if (message is null || message.Summary.Nid != summary.Nid)
                        throw new InvalidDataException("PST returned a different message during export.");
                    var path = Path.Combine(output, $"message-{summary.Nid:X8}.eml");
                    try { await exportMessage(message, path, cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IOException or ArgumentException)
                    {
                        var reason = ex switch
                        {
                            NotSupportedException => "it contains a message feature this EML exporter does not support",
                            InvalidDataException => "its data is incomplete or exceeds the export limits",
                            ArgumentException => "its address or attachment filename is unsafe",
                            _ => "an attachment or output file could not be read or written"
                        };
                        throw new PstFolderExportException(summary.Nid, reason, ex);
                    }
                    messagesExported++;
                    progress?.Report(new PstFolderExportProgress(foldersVisited + 1, messagesExported));
                }
                foldersVisited++;
                progress?.Report(new PstFolderExportProgress(foldersVisited, messagesExported));
            }
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSafeDirectory(parent);
            EnsureDestinationIsNew(destination);
            Directory.Move(temporary, destination);
            temporary = null;
            return new PstFolderExportResult(foldersVisited, messagesExported);
        }
        finally
        {
            if (temporary is not null)
            {
                try { Directory.Delete(temporary, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    public static string SuggestedDirectoryName(MailFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var text = new StringBuilder();
        var previousDash = false;
        foreach (var ch in folder.Name ?? "")
        {
            if (text.Length >= 40) break;
            if (char.IsAsciiLetterOrDigit(ch))
            {
                text.Append(ch);
                previousDash = false;
            }
            else if (!previousDash && text.Length > 0)
            {
                text.Append('-');
                previousDash = true;
            }
        }
        var label = text.ToString().Trim('-');
        return $"{(label.Length == 0 ? "folder" : label)}-{folder.Nid:X8}";
    }

    private static IReadOnlyList<(MailFolder Folder, string Relative)> Plan(MailFolder root)
    {
        var pending = new Stack<(MailFolder Folder, string Relative, int Depth)>();
        pending.Push((root, "", 0));
        var seen = new HashSet<uint>();
        var result = new List<(MailFolder, string)>();
        while (pending.Count > 0)
        {
            var (folder, relative, depth) = pending.Pop();
            if (depth > MaximumDepth || !seen.Add(folder.Nid) || result.Count >= MaximumFolders)
                throw new InvalidDataException("PST folder tree exceeds the export limits or contains a cycle.");
            result.Add((folder, relative));
            foreach (var child in folder.Children.AsEnumerable().Reverse())
                pending.Push((child, Path.Combine(relative, SuggestedDirectoryName(child)), depth + 1));
        }
        return result;
    }

    private static void EnsureDestinationIsNew(string path)
    {
        if (File.Exists(path) || Directory.Exists(path) || new DirectoryInfo(path).LinkTarget is not null)
            throw new IOException("Output folder already exists; export will not overwrite it.");
    }

    private static void EnsureSafeDirectory(string directory)
    {
        for (DirectoryInfo? current = new(directory); current is not null; current = current.Parent)
            if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Output directory does not exist or contains a symbolic link.");
    }
}
