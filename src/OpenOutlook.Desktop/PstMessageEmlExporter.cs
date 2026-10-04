using MailAddress = System.Net.Mail.MailAddress;
using System.Text;
using PstCore;

namespace OpenOutlook.Desktop;

/// <summary>Bounded MIME export from a read-only PST. Unsupported attachment forms fail closed.</summary>
public static class PstMessageEmlExporter
{
    public const int DefaultMaximumBodyBytes = 4 * 1024 * 1024;
    public const int MaximumAttachments = 32;
    public const long MaximumTotalAttachmentBytes = 128L * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Use this overload with a PST opened read-only; the caller obtains the MailMessage with OpenMessage.</summary>
    public static Task ExportAsync(IPstEngine store, MailMessage message, string destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (store.CanWrite) throw new InvalidOperationException("EML export requires a read-only PST store.");
        return ExportAsync(message, destination,
            attachment => store.ReadAttachmentData(message.Summary, attachment,
                (int)PstAttachmentExporter.DefaultMaximumBytes, cancellationToken), cancellationToken);
    }

    /// <summary>Also accepts synthetic messages without attachments for testing.</summary>
    public static Task ExportAsync(MailMessage message, string destination,
        CancellationToken cancellationToken = default) =>
        ExportAsync(message, destination, _ => throw new NotSupportedException("Attachment data is unavailable."),
            cancellationToken);

    /// <summary>The injected reader supports MIME tests without a PST.</summary>
    public static async Task ExportAsync(MailMessage message, string destination,
        Func<MailAttachment, byte[]> readAttachment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(message.Summary);
        ArgumentNullException.ThrowIfNull(readAttachment);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateMessage(message);
        if (string.IsNullOrWhiteSpace(destination) || !Path.IsPathFullyQualified(destination))
            throw new ArgumentException("Select an absolute output path.", nameof(destination));
        var filename = Path.GetFileName(destination);
        PstAttachmentExporter.ValidateSuggestedFileName(filename);
        if (!filename.EndsWith(".eml", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output filename must end in .eml.", nameof(destination));
        destination = Path.GetFullPath(destination);
        var directory = Path.GetDirectoryName(destination)!;
        EnsureSafeDirectory(directory);
        EnsureDestinationIsNew(destination);
        var mixedBoundary = message.Attachments.Count > 0 ? NewBoundary() : null;
        var alternativeBoundary = !string.IsNullOrEmpty(message.BodyText) && !string.IsNullOrEmpty(message.BodyHtml)
            ? NewBoundary() : null;
        var headers = BuildHeaders(message, mixedBoundary, alternativeBoundary);

        string? temporary = null;
        try
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = Path.Combine(directory, ".pst-eml-" + Guid.NewGuid().ToString("N") + ".tmp");
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
                    await using (var writer = new StreamWriter(output, Encoding.ASCII, leaveOpen: true))
                    {
                        writer.NewLine = "\r\n";
                        await writer.WriteAsync(headers.AsMemory(), cancellationToken).ConfigureAwait(false);
                        await WriteMimeBodyAsync(writer, message, readAttachment, mixedBoundary,
                            alternativeBoundary, cancellationToken).ConfigureAwait(false);
                        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (IOException) when (temporary is null && File.Exists(candidate))
                {
                    // Do not adopt or overwrite an existing temporary path.
                }
            }
            if (temporary is null) throw new IOException("Could not create an EML temporary file.");
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
                catch (IOException) { /* Preserve original failure. */ }
                catch (UnauthorizedAccessException) { /* Preserve original failure. */ }
            }
        }
    }

    private static void ValidateMessage(MailMessage message)
    {
        if (message.Attachments is null || message.Attachments.Count > MaximumAttachments ||
            message.Summary.HasAttachment && message.Attachments.Count == 0)
            throw new NotSupportedException("Attachment metadata is incomplete or exceeds the EML limit.");
        if (message.BodyHtml?.Contains("cid:", StringComparison.OrdinalIgnoreCase) == true)
            throw new NotSupportedException("HTML with embedded Content-ID resources is not yet supported in EML export.");
        _ = EncodeBody(message.BodyText);
        _ = EncodeBody(message.BodyHtml);
        long total = 0;
        foreach (var attachment in message.Attachments)
        {
            if (attachment.Method != 1 || attachment.IsHidden || !string.IsNullOrWhiteSpace(attachment.ContentId))
                throw new NotSupportedException("Embedded or non-file attachments cannot yet be exported to EML.");
            PstAttachmentExporter.ValidateSuggestedFileName(attachment.FileName);
            if (attachment.Size < 0 || attachment.Size > PstAttachmentExporter.DefaultMaximumBytes)
                throw new InvalidDataException("Attachment exceeds the EML size limit.");
            total += attachment.Size;
            if (total > MaximumTotalAttachmentBytes)
                throw new InvalidDataException("Message attachments exceed the EML size limit.");
        }
    }

    private static string BuildHeaders(MailMessage message, string? mixedBoundary, string? alternativeBoundary)
    {
        var headers = new StringBuilder("MIME-Version: 1.0\r\n");
        AddHeader(headers, "From", EncodeMailbox(message.Summary.From));
        var toRecipients = message.Recipients.Where(recipient => recipient.Kind == RecipientKind.To).ToArray();
        if (toRecipients.Length > 0)
            AddHeader(headers, "To", string.Join(", ", toRecipients.Select(EncodeRecipient)));
        else if (!string.IsNullOrWhiteSpace(message.Summary.To) && !message.Summary.To.Contains('@'))
            AddHeader(headers, "X-OpenOutlook-To-Display", EncodeWords(CleanHeader(message.Summary.To)));
        else
            AddHeader(headers, "To", EncodeMailboxes(message.Summary.To));
        foreach (var (kind, header) in new[] { (RecipientKind.Cc, "Cc"), (RecipientKind.Bcc, "Bcc") })
        {
            var recipients = message.Recipients.Where(recipient => recipient.Kind == kind).ToArray();
            if (recipients.Length > 0)
                AddHeader(headers, header, string.Join(", ", recipients.Select(EncodeRecipient)));
        }
        AddHeader(headers, "Subject", EncodeWords(CleanHeader(message.Summary.Subject)));
        var date = message.Summary.Sent != DateTime.MinValue ? message.Summary.Sent : message.Summary.Received;
        if (date != DateTime.MinValue)
        {
            // Unspecified PST wall-clock times have no reliable zone; use UTC only for known UTC/local kinds.
            if (date.Kind != DateTimeKind.Unspecified)
                AddHeader(headers, "Date", new DateTimeOffset(date.ToUniversalTime(), TimeSpan.Zero)
                    .ToString("ddd, dd MMM yyyy HH:mm:ss +0000", System.Globalization.CultureInfo.InvariantCulture));
        }
        if (mixedBoundary is not null)
            AddHeader(headers, "Content-Type", $"multipart/mixed; boundary=\"{mixedBoundary}\"");
        else if (alternativeBoundary is not null)
            AddHeader(headers, "Content-Type", $"multipart/alternative; boundary=\"{alternativeBoundary}\"");
        else
        {
            AddHeader(headers, "Content-Type", string.IsNullOrEmpty(message.BodyHtml)
                ? "text/plain; charset=utf-8" : "text/html; charset=utf-8");
            AddHeader(headers, "Content-Transfer-Encoding", "base64");
        }
        return headers.Append("\r\n").ToString();
    }

    private static string NewBoundary() => "OpenOutlook_" + Guid.NewGuid().ToString("N");

    private static async Task WriteMimeBodyAsync(StreamWriter writer, MailMessage message,
        Func<MailAttachment, byte[]> readAttachment, string? mixedBoundary, string? alternativeBoundary,
        CancellationToken cancellationToken)
    {
        if (mixedBoundary is not null)
            await writer.WriteLineAsync($"--{mixedBoundary}".AsMemory(), cancellationToken).ConfigureAwait(false);
        if (alternativeBoundary is not null)
        {
            if (mixedBoundary is not null)
            {
                await writer.WriteLineAsync($"Content-Type: multipart/alternative; boundary=\"{alternativeBoundary}\"".AsMemory(),
                    cancellationToken).ConfigureAwait(false);
                await writer.WriteLineAsync(ReadOnlyMemory<char>.Empty, cancellationToken).ConfigureAwait(false);
            }
            await writer.WriteLineAsync($"--{alternativeBoundary}".AsMemory(), cancellationToken).ConfigureAwait(false);
            await WriteTextPartAsync(writer, "text/plain", message.BodyText, cancellationToken).ConfigureAwait(false);
            await writer.WriteLineAsync($"--{alternativeBoundary}".AsMemory(), cancellationToken).ConfigureAwait(false);
            await WriteTextPartAsync(writer, "text/html", message.BodyHtml, cancellationToken).ConfigureAwait(false);
            await writer.WriteLineAsync($"--{alternativeBoundary}--".AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        else if (mixedBoundary is not null)
            await WriteTextPartAsync(writer, string.IsNullOrEmpty(message.BodyHtml) ? "text/plain" : "text/html",
                string.IsNullOrEmpty(message.BodyHtml) ? message.BodyText : message.BodyHtml,
                cancellationToken).ConfigureAwait(false);
        else
            await WriteBase64Async(writer, EncodeBody(string.IsNullOrEmpty(message.BodyHtml)
                ? message.BodyText : message.BodyHtml), cancellationToken).ConfigureAwait(false);

        if (mixedBoundary is null) return;
        long total = 0;
        foreach (var attachment in message.Attachments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes;
            try { bytes = readAttachment(attachment); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { throw new IOException("PST attachment could not be read for EML export."); }
            if (bytes is null || bytes.Length == 0 || bytes.LongLength > PstAttachmentExporter.DefaultMaximumBytes ||
                total + bytes.LongLength > MaximumTotalAttachmentBytes)
                throw new InvalidDataException("Attachment has no exportable data or exceeds the EML size limit.");
            total += bytes.LongLength;
            await writer.WriteLineAsync($"--{mixedBoundary}".AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.WriteLineAsync($"Content-Type: {SafeMimeType(attachment.MimeTag)}".AsMemory(),
                cancellationToken).ConfigureAwait(false);
            await writer.WriteLineAsync("Content-Transfer-Encoding: base64".AsMemory(),
                cancellationToken).ConfigureAwait(false);
            await writer.WriteLineAsync(("Content-Disposition: attachment; " + EncodeFilename(attachment.FileName))
                .AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.WriteLineAsync(ReadOnlyMemory<char>.Empty, cancellationToken).ConfigureAwait(false);
            await WriteBase64Async(writer, bytes, cancellationToken).ConfigureAwait(false);
        }
        await writer.WriteLineAsync($"--{mixedBoundary}--".AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteTextPartAsync(StreamWriter writer, string mediaType, string? body,
        CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync($"Content-Type: {mediaType}; charset=utf-8".AsMemory(),
            cancellationToken).ConfigureAwait(false);
        await writer.WriteLineAsync("Content-Transfer-Encoding: base64".AsMemory(),
            cancellationToken).ConfigureAwait(false);
        await writer.WriteLineAsync(ReadOnlyMemory<char>.Empty, cancellationToken).ConfigureAwait(false);
        await WriteBase64Async(writer, EncodeBody(body), cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteBase64Async(StreamWriter writer, byte[] bytes, CancellationToken cancellationToken)
    {
        const int inputChunk = 57 * 512; // 57 bytes encode to exactly one 76-character MIME line.
        if (bytes.Length == 0)
        {
            await writer.WriteLineAsync(ReadOnlyMemory<char>.Empty, cancellationToken).ConfigureAwait(false);
            return;
        }
        for (var offset = 0; offset < bytes.Length; offset += inputChunk)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var encoded = Convert.ToBase64String(bytes, offset, Math.Min(inputChunk, bytes.Length - offset));
            var lines = new StringBuilder(encoded.Length + encoded.Length / 76 * 2 + 2);
            for (var i = 0; i < encoded.Length; i += 76)
                lines.Append(encoded, i, Math.Min(76, encoded.Length - i)).Append("\r\n");
            await writer.WriteAsync(lines.ToString().AsMemory(), cancellationToken).ConfigureAwait(false);
        }
    }

    private static byte[] EncodeBody(string? body)
    {
        body ??= "";
        if (body.Length > DefaultMaximumBodyBytes ||
            body.Any(ch => char.IsControl(ch) && ch is not ('\r' or '\n' or '\t') || ch == '\u007f'))
            throw new InvalidDataException("Message body exceeds the limit or contains control characters.");
        try
        {
            var bytes = StrictUtf8.GetBytes(body);
            if (bytes.Length > DefaultMaximumBodyBytes)
                throw new InvalidDataException("Message body exceeds the permitted length.");
            return bytes;
        }
        catch (EncoderFallbackException) { throw new InvalidDataException("Message body contains invalid Unicode."); }
    }

    private static string SafeMimeType(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 127 || value.Count(ch => ch == '/') != 1 ||
            value.Split('/').Any(part => part.Length == 0 || part.Any(ch =>
                !(char.IsAsciiLetterOrDigit(ch) || ch is '!' or '#' or '$' or '&' or '^' or '_' or '.' or '+' or '-'))))
            return "application/octet-stream";
        return value;
    }

    private static string EncodeFilename(string name)
    {
        PstAttachmentExporter.ValidateSuggestedFileName(name);
        if (name.All(ch => ch is >= ' ' and <= '~') && name.Length <= 60)
            return $"filename=\"{name}\"";
        var bytes = StrictUtf8.GetBytes(name);
        var result = new StringBuilder();
        for (var offset = 0; offset < bytes.Length; offset += 15)
        {
            if (offset > 0) result.Append(";\r\n ");
            result.Append("filename*").Append(offset / 15).Append("*=");
            if (offset == 0) result.Append("utf-8''");
            for (var i = offset; i < Math.Min(offset + 15, bytes.Length); i++)
                result.Append('%').Append(bytes[i].ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return result.ToString();
    }

    private static void AddHeader(StringBuilder headers, string name, string value)
    {
        if (value.Length != 0) headers.Append(name).Append(": ").Append(value).Append("\r\n");
    }

    private static string CleanHeader(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        // Reject rather than unfold attacker-controlled newlines or invisible control characters.
        if (text.Any(ch => char.IsControl(ch) || ch == '\u007f' || char.IsSurrogate(ch)))
            throw new InvalidDataException("Message header contains unsupported characters.");
        if (StrictUtf8.GetByteCount(text) > 4096)
            throw new InvalidDataException("Message header exceeds the permitted length.");
        return text.Trim();
    }

    private static string EncodeWords(string text)
    {
        if (text.Length == 0) return text;
        // RFC 2047 encoded-word, each chunk at most 45 UTF-8 bytes (<= 72 chars with delimiters).
        var result = new StringBuilder();
        var chunk = new List<byte>();
        Span<byte> buffer = stackalloc byte[4];
        foreach (var rune in text.EnumerateRunes())
        {
            var length = rune.EncodeToUtf8(buffer);
            if (chunk.Count + length > 45)
            {
                if (result.Length > 0) result.Append("\r\n ");
                result.Append("=?utf-8?B?").Append(Convert.ToBase64String(chunk.ToArray())).Append("?=");
                chunk.Clear();
            }
            for (var i = 0; i < length; i++) chunk.Add(buffer[i]);
        }
        if (chunk.Count > 0)
        {
            if (result.Length > 0) result.Append("\r\n ");
            result.Append("=?utf-8?B?").Append(Convert.ToBase64String(chunk.ToArray())).Append("?=");
        }
        return result.ToString();
    }

    private static string EncodeMailboxes(string? text)
    {
        text = CleanHeader(text);
        if (text.Length == 0) return "";
        // PST DisplayTo commonly separates recipients with semicolons.
        if (text.Contains(';')) return string.Join(", ", text.Split(';').Select(EncodeMailbox));
        if (text.Contains('<')) return EncodeMailbox(text);
        return string.Join(", ", text.Split(',').Select(EncodeMailbox));
    }

    private static string EncodeRecipient(MailRecipient recipient)
    {
        var mailbox = CleanHeader(recipient.Email);
        if (!MailAddress.TryCreate(mailbox, out var parsed) || parsed is null ||
            !string.Equals(parsed.Address, mailbox, StringComparison.Ordinal) ||
            mailbox.Any(ch => ch is < '!' or > '~' || ch is ',' or ';' or '<' or '>') ||
            !mailbox.Contains('@'))
            throw new InvalidDataException("PST recipient address cannot be safely represented in EML.");
        var name = CleanHeader(recipient.Name);
        return name.Length == 0 ? mailbox : EncodeWords(name) + " <" + mailbox + ">";
    }

    private static string EncodeMailbox(string? text)
    {
        text = CleanHeader(text);
        if (text.Length == 0) return "";
        MailAddress? address;
        string displayName;
        if (MailAddress.TryCreate(text, out address) && address is not null)
            displayName = address.DisplayName;
        else
        {
            // Older PSTs often store an unquoted comma in the display name:
            // "Last, First <address@example.com>". Parse only the unambiguous angle form.
            var open = text.LastIndexOf('<');
            var close = text.LastIndexOf('>');
            if (open < 0 || close != text.Length - 1 || open >= close ||
                text[..open].Contains('<') || text[..open].Contains('>'))
                throw new InvalidDataException("Message address cannot be safely represented as an email mailbox.");
            var mailbox = text[(open + 1)..close];
            if (!MailAddress.TryCreate(mailbox, out address) || address is null ||
                !string.Equals(address.Address, mailbox, StringComparison.Ordinal))
                throw new InvalidDataException("Message address cannot be safely represented as an email mailbox.");
            displayName = text[..open].Trim().Trim('"');
        }
        if (address is null ||
            address.Address.Any(ch => ch is < '!' or > '~' || ch is ',' or ';' or '<' or '>') ||
            !address.Address.Contains('@') || displayName.Any(ch => char.IsControl(ch)))
            throw new InvalidDataException("Message address cannot be safely represented as an email mailbox.");
        var name = CleanHeader(displayName);
        if (name.Length == 0) return address.Address;
        // Always encode display names, including ASCII, to avoid delimiter/quote injection.
        var encodedName = EncodeWords(name);
        if (encodedName == name)
            encodedName = "=?utf-8?B?" + Convert.ToBase64String(StrictUtf8.GetBytes(name)) + "?=";
        return encodedName + " <" + address.Address + ">";
    }

    private static void EnsureDestinationIsNew(string path)
    {
        if (File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null)
            throw new IOException("Output path already exists; export will not overwrite it.");
    }

    private static void EnsureSafeDirectory(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Output directory does not exist or contains a symbolic link.");
    }
}
