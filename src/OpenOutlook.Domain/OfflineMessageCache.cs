using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenOutlook.Domain;

/// <summary>
/// Plain-text local mail snapshot. The caller must supply an already verified provider/account identity;
/// this type never authenticates, syncs, contacts a provider, or writes mail to a provider.
/// Local content is NOT encrypted: use only on a trusted device with protected backups.
/// </summary>
public sealed record CachedMail(
    MailProvider Provider, string AccountId, string FolderId, string MessageId, string Revision,
    string Subject, string From, string To, string Body, DateTimeOffset ReceivedUtc);

/// <summary>
/// Account-scoped, bounded, atomic JSON cache. IDs are opaque: only SHA-256 digests, never IDs,
/// appear in disk paths. One snapshot per provider/account, keyed by message ID.
/// Not a credential store. No arbitrary JSON or token fields are accepted on disk.
/// </summary>
public sealed class OfflineMessageCache
{
    public const int MaxMessagesPerAccount = 500;
    public const int MaxAccounts = 128;
    public const int MaxFileBytes = 4 * 1024 * 1024;
    private const int MaxIdLength = 512;
    private const int MaxBodyLength = 256 * 1024;
    private readonly string _root;
    private static readonly UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly UnixFileMode FileModePrivate = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    /// <param name="root">Absolute private directory; null uses XDG_DATA_HOME/openoutlook/offline-mail-v1,
    /// or HOME/.local/share/openoutlook/offline-mail-v1. An existing directory must already be private.</param>
    public OfflineMessageCache(string? root = null)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Private offline cache requires Unix file permissions.");
        if (root is null)
        {
            var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrWhiteSpace(data))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrWhiteSpace(home)) throw new InvalidOperationException("No user data directory available.");
                data = Path.Combine(home, ".local", "share");
            }
            if (!Path.IsPathFullyQualified(data)) throw new ArgumentException("XDG_DATA_HOME must be absolute.");
            root = Path.Combine(data, "openoutlook", "offline-mail-v1");
        }
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("Cache root must be absolute.", nameof(root));
        _root = Path.GetFullPath(root);
        // Do not create data before first operation; each operation rechecks path and permissions.
    }

    public void Upsert(CachedMail mail)
    {
        Validate(mail);
        WithAccount(mail.Provider, mail.AccountId, path =>
        {
            var messages = ReadSnapshot(path, mail.Provider, mail.AccountId);
            var index = messages.FindIndex(m => string.Equals(m.MessageId, mail.MessageId, StringComparison.Ordinal));
            if (index >= 0) messages[index] = mail;
            else
            {
                if (messages.Count >= MaxMessagesPerAccount) throw new InvalidOperationException("Account cache message limit exceeded.");
                messages.Add(mail);
            }
            WriteSnapshot(path, mail.Provider, mail.AccountId, messages);
            return true;
        });
    }

    public IReadOnlyList<CachedMail> List(MailProvider provider, string accountId) =>
        WithAccount(provider, accountId, path => ReadSnapshot(path, provider, accountId).AsReadOnly());

    public CachedMail? Get(MailProvider provider, string accountId, string messageId)
    {
        ValidateId(messageId, nameof(messageId));
        return WithAccount(provider, accountId, path => ReadSnapshot(path, provider, accountId)
            .Find(m => string.Equals(m.MessageId, messageId, StringComparison.Ordinal)));
    }

    /// <returns>True if a locally cached message was removed; this never deletes provider mail.</returns>
    public bool Delete(MailProvider provider, string accountId, string messageId)
    {
        ValidateId(messageId, nameof(messageId));
        return WithAccount(provider, accountId, path =>
        {
            var messages = ReadSnapshot(path, provider, accountId);
            if (messages.RemoveAll(m => string.Equals(m.MessageId, messageId, StringComparison.Ordinal)) == 0) return false;
            WriteSnapshot(path, provider, accountId, messages);
            return true;
        });
    }

    private T WithAccount<T>(MailProvider provider, string accountId, Func<string, T> operation)
    {
        ValidateProvider(provider);
        ValidateId(accountId, nameof(accountId));
        EnsureRoot();
        var name = Digest(provider + "\n" + accountId);
        var path = Path.Combine(_root, name + ".json");
        var lockPath = Path.Combine(_root, name + ".lock");
        CheckNoUnexpectedFiles();
        if (!File.Exists(lockPath) && Directory.EnumerateFiles(_root, "*.lock").Count() >= MaxAccounts)
            throw new InvalidOperationException("Cache account limit exceeded.");
        CheckFile(lockPath);
        // A persistent empty lock file serializes independent processes/instances. Contention fails closed.
        var lockOptions = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None,
            Options = FileOptions.WriteThrough
        };
        if (!OperatingSystem.IsWindows()) lockOptions.UnixCreateMode = FileModePrivate;
        using var guard = new FileStream(lockPath, lockOptions);
        CheckFile(lockPath);
        if (guard.Length != 0) throw new InvalidDataException("Corrupt cache lock file.");
        EnsureRoot();
        CheckFile(path);
        CheckNoUnexpectedFiles();
        return operation(path);
    }

    private void CheckNoUnexpectedFiles()
    {
        int accounts = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(_root))
        {
            var name = Path.GetFileName(entry);
            if (name.Length != 69 || !IsDigest(name[..64]) ||
                (name[64..] != ".json" && name[64..] != ".lock"))
                throw new InvalidDataException("Unexpected cache entry; inspect rather than silently recovering.");
            CheckFile(entry);
            if (name.EndsWith(".lock", StringComparison.Ordinal)) accounts++;
        }
        if (accounts > MaxAccounts) throw new InvalidDataException("Cache account limit exceeded.");
    }

    private static bool IsDigest(string text) => text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private void EnsureRoot()
    {
        // Check every ancestor, including the root; never follow symlinks/reparse points intentionally.
        var chain = new Stack<string>();
        var at = _root;
        while (true)
        {
            chain.Push(at);
            var parent = Path.GetDirectoryName(at);
            if (parent is null || parent == at) break;
            at = parent;
        }
        while (chain.TryPop(out var directory))
        {
            if (File.Exists(directory) || Directory.Exists(directory))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 || !Directory.Exists(directory))
                    throw new IOException("Cache path contains a link or non-directory.");
                if (IsManaged(directory)) CheckDirectoryMode(directory);
            }
            else
            {
                // Broken links also have attributes; do not interpret them as absent directories.
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Cache path contains a broken link.");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                Directory.CreateDirectory(directory, DirectoryMode);
                CheckDirectoryMode(directory);
            }
        }
    }

    private bool IsManaged(string directory) =>
        string.Equals(directory, _root, StringComparison.Ordinal);

    private static void CheckDirectoryMode(string directory)
    {
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(directory) & ~DirectoryMode) != 0)
            throw new UnauthorizedAccessException("Existing cache directory is not private (requires 0700).");
    }

    private static void CheckFile(string path)
    {
        if (!File.Exists(path))
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Cache file is a link.");
            }
            catch (FileNotFoundException) { }
            return;
        }
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) != 0)
            throw new IOException("Cache file is a link or directory.");
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & ~FileModePrivate) != 0)
            throw new UnauthorizedAccessException("Existing cache file is not private (requires 0600).");
    }

    private static List<CachedMail> ReadSnapshot(string path, MailProvider provider, string accountId)
    {
        CheckFile(path);
        if (!File.Exists(path)) return new List<CachedMail>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxFileBytes) throw new InvalidDataException("Cache file exceeds size limit.");
        // Limit bytes actually read too, even when the file changes during the read.
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = stream.Read(buffer)) != 0)
        {
            if (memory.Length + read > MaxFileBytes) throw new InvalidDataException("Cache file exceeds size limit.");
            memory.Write(buffer, 0, read);
        }
        Snapshot? snapshot;
        try { snapshot = JsonSerializer.Deserialize<Snapshot>(memory.ToArray(), JsonOptions); }
        catch (JsonException) { throw new InvalidDataException("Corrupt cache snapshot."); }
        if (snapshot is null || snapshot.Version != 1 || snapshot.Provider != provider ||
            !string.Equals(snapshot.AccountId, accountId, StringComparison.Ordinal) ||
            snapshot.Messages is null || snapshot.Messages.Count > MaxMessagesPerAccount)
            throw new InvalidDataException("Invalid cache snapshot or account identity.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mail in snapshot.Messages)
        {
            try { Validate(mail); }
            catch (ArgumentException ex) { throw new InvalidDataException("Invalid cached mail.", ex); }
            if (mail.Provider != provider || !string.Equals(mail.AccountId, accountId, StringComparison.Ordinal) ||
                !ids.Add(mail.MessageId)) throw new InvalidDataException("Mixed-account or duplicate cache entry.");
        }
        return snapshot.Messages;
    }

    private static void WriteSnapshot(string path, MailProvider provider, string accountId, List<CachedMail> messages)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Snapshot(1, provider, accountId, messages), JsonOptions);
        if (bytes.Length > MaxFileBytes) throw new InvalidOperationException("Cache file size limit exceeded.");
        var temp = Path.Combine(Path.GetDirectoryName(path)!, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var tempOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                Options = FileOptions.WriteThrough
            };
            if (!OperatingSystem.IsWindows()) tempOptions.UnixCreateMode = FileModePrivate;
            using (var stream = new FileStream(temp, tempOptions))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            CheckFile(path);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static void Validate(CachedMail? mail)
    {
        if (mail is null) throw new ArgumentException("Cached mail is required.", nameof(mail));
        ValidateProvider(mail.Provider);
        ValidateId(mail.AccountId, nameof(mail.AccountId));
        ValidateId(mail.FolderId, nameof(mail.FolderId));
        ValidateId(mail.MessageId, nameof(mail.MessageId));
        ValidateId(mail.Revision, nameof(mail.Revision));
        ValidateText(mail.Subject, 4096, nameof(mail.Subject));
        ValidateText(mail.From, 2048, nameof(mail.From));
        ValidateText(mail.To, 2048, nameof(mail.To));
        ValidateText(mail.Body, MaxBodyLength, nameof(mail.Body));
        if (mail.ReceivedUtc == default || mail.ReceivedUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Timestamp must be UTC.", nameof(mail.ReceivedUtc));
    }

    private static void ValidateProvider(MailProvider provider)
    {
        if (provider is not (MailProvider.Microsoft or MailProvider.Gmail))
            throw new ArgumentException("Only Microsoft and Gmail cache identities are supported.", nameof(provider));
    }

    private static void ValidateId(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxIdLength || value.Any(char.IsControl))
            throw new ArgumentException("A nonempty, bounded opaque identifier is required.", name);
    }

    private static void ValidateText(string? value, int max, string name)
    {
        if (value is null || value.Length > max) throw new ArgumentException("Text exceeds cache bounds or is null.", name);
    }

    private sealed record Snapshot(int Version, MailProvider Provider, string AccountId, List<CachedMail> Messages);
}
