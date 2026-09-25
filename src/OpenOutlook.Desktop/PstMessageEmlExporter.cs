using MailAddress = System.Net.Mail.MailAddress;
using System.Text;
using PstCore;

namespace OpenOutlook.Desktop;

/// <summary>Conservative, text-only EML export. Does not call PstCore.EmlExport or read attachment data.</summary>
public static class PstMessageEmlExporter
{
    public const int DefaultMaximumBodyBytes = 4 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Use this overload with a PST opened read-only; the caller obtains the MailMessage with OpenMessage.</summary>
    public static Task ExportAsync(PstStore store, MailMessage message, string destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (store.CanWrite) throw new InvalidOperationException("EML export requires a read-only PST store.");
        return ExportAsync(message, destination, cancellationToken);
    }

    /// <summary>Also accepts synthetic messages for testing; never reads or writes a PST.</summary>
    public static async Task ExportAsync(MailMessage message, string destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(message.Summary);
        cancellationToken.ThrowIfCancellationRequested();
        if (message.Attachments is null || message.Attachments.Count != 0 || message.Summary.HasAttachment)
            throw new NotSupportedException("Messages with attachments cannot be exported as text-only EML.");
        if (string.IsNullOrEmpty(message.BodyText) && !string.IsNullOrEmpty(message.BodyHtml))
            throw new NotSupportedException("HTML-only messages cannot be exported as text-only EML.");
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
        var payload = Serialize(message, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

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
                    await output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
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

    private static byte[] Serialize(MailMessage message, CancellationToken cancellationToken)
    {
        var body = message.BodyText ?? "";
        if (body.Length > DefaultMaximumBodyBytes) throw new InvalidDataException("Message body exceeds the permitted length.");
        // UTF-8 encoding is strict: reject malformed surrogate pairs rather than silently replacing them.
        byte[] bodyBytes;
        try { bodyBytes = StrictUtf8.GetBytes(body); }
        catch (EncoderFallbackException) { throw new InvalidDataException("Message body contains invalid Unicode."); }
        if (bodyBytes.Length > DefaultMaximumBodyBytes)
            throw new InvalidDataException("Message body exceeds the permitted length.");
        if (body.Any(ch => (char.IsControl(ch) && ch is not ('\r' or '\n' or '\t')) || ch == '\u007f'))
            throw new InvalidDataException("Message body contains unsupported control characters.");
        cancellationToken.ThrowIfCancellationRequested();
        var headers = new StringBuilder("MIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n");
        AddHeader(headers, "From", EncodeMailbox(message.Summary.From));
        AddHeader(headers, "To", EncodeMailboxes(message.Summary.To));
        AddHeader(headers, "Subject", EncodeWords(CleanHeader(message.Summary.Subject)));
        var date = message.Summary.Sent != DateTime.MinValue ? message.Summary.Sent : message.Summary.Received;
        if (date != DateTime.MinValue)
        {
            // Unspecified PST wall-clock times have no reliable zone; use UTC only for known UTC/local kinds.
            if (date.Kind != DateTimeKind.Unspecified)
                AddHeader(headers, "Date", new DateTimeOffset(date.ToUniversalTime(), TimeSpan.Zero)
                    .ToString("ddd, dd MMM yyyy HH:mm:ss +0000", System.Globalization.CultureInfo.InvariantCulture));
        }
        headers.Append("\r\n");
        var encoded = Convert.ToBase64String(bodyBytes);
        for (var i = 0; i < encoded.Length; i += 76)
        {
            cancellationToken.ThrowIfCancellationRequested();
            headers.Append(encoded, i, Math.Min(76, encoded.Length - i)).Append("\r\n");
        }
        // Empty body still ends with CRLF after the header separator.
        return Encoding.ASCII.GetBytes(headers.ToString());
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
        return string.Join(", ", text.Replace(';', ',').Split(',').Select(EncodeMailbox));
    }

    private static string EncodeMailbox(string? text)
    {
        text = CleanHeader(text);
        if (text.Length == 0) return "";
        if (!MailAddress.TryCreate(text, out var address) || address is null ||
            address.Address.Any(ch => ch is < '!' or > '~' || ch is ',' or ';' or '<' or '>') ||
            !address.Address.Contains('@') || address.DisplayName.Any(ch => char.IsControl(ch)))
            throw new InvalidDataException("Message address cannot be safely represented as an email mailbox.");
        var name = CleanHeader(address.DisplayName);
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
