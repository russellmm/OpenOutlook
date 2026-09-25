using PstCore;

namespace OpenOutlook.Desktop;

/// <summary>Exports only by-value PST attachments to a new file; never modifies the PST.</summary>
public static class PstAttachmentExporter
{
    public const long DefaultMaximumBytes = 64L * 1024 * 1024;

    /// <summary>Read the attachment from a store opened with <c>PstStore.Open(path)</c> (read-only).</summary>
    public static Task ExportAsync(PstStore store, MailSummary message, MailAttachment attachment,
        string selectedOutputPath, long maximumBytes = DefaultMaximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(message);
        if (store.CanWrite)
            throw new InvalidOperationException("Attachment export requires a read-only PST store.");
        if (maximumBytes <= 0 || maximumBytes > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        return ExportAsync(attachment, selectedOutputPath,
            () => store.ReadAttachmentData(message, attachment, (int)maximumBytes, cancellationToken), maximumBytes, cancellationToken);
    }

    /// <summary>
    /// The injected reader permits exporting synthetic bytes without a PST. A custom reader is
    /// synchronous and cannot be interrupted or bounded before returning; the store overload uses
    /// PstCore's bounded reader instead.
    /// </summary>
    public static async Task ExportAsync(MailAttachment attachment, string selectedOutputPath,
        Func<byte[]> readData, long maximumBytes = DefaultMaximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(readData);
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (attachment.Method != 1)
            throw new NotSupportedException("Only by-value attachments are supported.");
        if (attachment.Size < 0 || attachment.Size > maximumBytes)
            throw new InvalidDataException("Attachment metadata exceeds the permitted length.");
        ValidateSuggestedFileName(attachment.FileName);
        if (string.IsNullOrWhiteSpace(selectedOutputPath) || !Path.IsPathFullyQualified(selectedOutputPath))
            throw new ArgumentException("Select an absolute output path.", nameof(selectedOutputPath));
        ValidateSuggestedFileName(Path.GetFileName(selectedOutputPath));
        var destination = Path.GetFullPath(selectedOutputPath);
        var directory = Path.GetDirectoryName(destination)!;
        EnsureSafeDirectory(directory);
        EnsureDestinationIsNew(destination);
        cancellationToken.ThrowIfCancellationRequested();

        byte[] bytes;
        try
        {
            bytes = readData();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Do not leak arbitrary reader error text (which could contain attachment content).
            throw new IOException("Attachment could not be read.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes is null || bytes.Length == 0)
            throw new InvalidDataException("Attachment contains no exportable data.");
        if (bytes.LongLength > maximumBytes)
            throw new InvalidDataException("Attachment exceeds the permitted length.");
        // A zero size is generally missing metadata; only a positive size can be compared reliably.
        if (attachment.Size > 0 && bytes.Length != attachment.Size)
            throw new InvalidDataException("Attachment length differs from its metadata.");

        string? temporary = null;
        try
        {
            // CreateNew prevents collisions; keep the temporary file in the destination directory
            // so the final move is atomic on the same filesystem.
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var candidate = Path.Combine(directory, ".pst-attachment-" + Guid.NewGuid().ToString("N") + ".tmp");
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
                    await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (IOException) when (temporary is null && File.Exists(candidate))
                {
                    // Extremely unlikely GUID collision; never adopt an existing file.
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
                catch (IOException) { /* Preserve the original failure. */ }
                catch (UnauthorizedAccessException) { /* Preserve the original failure. */ }
            }
        }
    }

    /// <summary>Validate a filename suggested by untrusted PST metadata; never interpret it as a path.</summary>
    public static string ValidateSuggestedFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name != name.Trim() ||
            name.EndsWith('.') || name.Length > 255 ||
            name.IndexOfAny(['/', '\\', ':', '<', '>', '"', '|', '?', '*']) >= 0 ||
            name.Any(char.IsControl) ||
            name.Split('.')[0].ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL" or
                "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9" or
                "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9")
            throw new ArgumentException("Unsafe attachment filename.", nameof(name));
        return name;
    }

    private static void EnsureDestinationIsNew(string path)
    {
        // Reject all pre-existing targets (including symlinks, hardlinks and broken symlinks).
        if (File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null)
            throw new IOException("Output path already exists; export will not overwrite it.");
    }

    private static void EnsureSafeDirectory(string directory)
    {
        var current = new DirectoryInfo(directory);
        while (current is not null)
        {
            if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Output directory does not exist or contains a symbolic link.");
            current = current.Parent;
        }
    }

}
