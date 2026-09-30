using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Desktop;

/// <summary>Saves a Graph file attachment to a new private file without overwriting a target.</summary>
public static class GraphAttachmentExporter
{
    public static Task ExportAsync(GraphAttachmentReader reader, string accessToken, string messageId,
        GraphAttachment attachment, string selectedOutputPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return ExportAsync(attachment, selectedOutputPath,
            (output, ct) => reader.CopyFileAsync(accessToken, messageId, attachment, output, ct), cancellationToken);
    }

    public static async Task ExportAsync(GraphAttachment attachment, string selectedOutputPath,
        Func<Stream, CancellationToken, Task<long>> copy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(copy);
        if (attachment.Kind != GraphAttachmentKind.File)
            throw new NotSupportedException("Only file attachments can be saved.");
        if (attachment.SizeBytes < 0 || attachment.SizeBytes > GraphAttachmentReader.MaximumFileBytes)
            throw new InvalidDataException("Attachment exceeds the size limit.");
        PstAttachmentExporter.ValidateSuggestedFileName(attachment.Name);
        if (string.IsNullOrWhiteSpace(selectedOutputPath) || !Path.IsPathFullyQualified(selectedOutputPath))
            throw new ArgumentException("Select an absolute output path.", nameof(selectedOutputPath));
        PstAttachmentExporter.ValidateSuggestedFileName(Path.GetFileName(selectedOutputPath));
        var destination = Path.GetFullPath(selectedOutputPath);
        var directory = Path.GetDirectoryName(destination)!;
        EnsureSafeDirectory(directory);
        EnsureDestinationIsNew(destination);
        cancellationToken.ThrowIfCancellationRequested();

        string? temporary = null;
        try
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var candidate = Path.Combine(directory, ".graph-attachment-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    var options = new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                        Options = FileOptions.Asynchronous, BufferSize = 81920
                    };
                    if (!OperatingSystem.IsWindows())
                        options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                    await using var output = new FileStream(candidate, options);
                    temporary = candidate;
                    var length = await copy(output, cancellationToken).ConfigureAwait(false);
                    if (length < 0 || length > GraphAttachmentReader.MaximumFileBytes || output.Length != length)
                        throw new InvalidDataException("Attachment copy returned an invalid length.");
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (IOException) when (temporary is null && File.Exists(candidate))
                {
                    // Never adopt an existing temporary file.
                }
            }
            if (temporary is null) throw new IOException("Could not create an export temporary file.");
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSafeDirectory(directory);
            EnsureDestinationIsNew(destination);
            File.Move(temporary, destination, overwrite: false);
            temporary = null;
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static void EnsureDestinationIsNew(string path)
    {
        if (File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null)
            throw new IOException("Output path already exists; no file was overwritten.");
    }

    private static void EnsureSafeDirectory(string directory)
    {
        for (DirectoryInfo? current = new(directory); current is not null; current = current.Parent)
            if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Output directory does not exist or contains a symbolic link.");
    }
}
