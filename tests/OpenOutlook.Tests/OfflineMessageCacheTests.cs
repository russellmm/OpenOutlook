using OpenOutlook.Domain;

namespace OpenOutlook.Tests;

public sealed class OfflineMessageCacheTests
{
    private static CachedMail Mail(string account = "account-A", string id = "message-A", string revision = "r1",
        MailProvider provider = MailProvider.Microsoft) =>
        new(provider, account, "folder-A", id, revision, "Subject", "sender@example.com",
            "recipient@example.com", "Plain text only", new DateTimeOffset(2024, 5, 4, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Snapshot_survives_restart_and_remains_account_and_provider_isolated()
    {
        using var temp = new TestRoot();
        var first = new OfflineMessageCache(temp.Path);
        first.Upsert(Mail());
        first.Upsert(Mail("account-B", "message-A"));
        first.Upsert(Mail("account-A", "message-A", provider: MailProvider.Gmail));
        var reopened = new OfflineMessageCache(temp.Path);
        Assert.Equal(Mail(), reopened.Get(MailProvider.Microsoft, "account-A", "message-A"));
        Assert.Single(reopened.List(MailProvider.Microsoft, "account-A"));
        Assert.Single(reopened.List(MailProvider.Microsoft, "account-B"));
        Assert.Single(reopened.List(MailProvider.Gmail, "account-A"));
        Assert.Null(reopened.Get(MailProvider.Gmail, "account-B", "message-A"));
        Assert.DoesNotContain("account-A", string.Join(" ", Directory.GetFileSystemEntries(temp.Path)));
    }

    [Fact]
    public void Revision_upsert_replaces_only_matching_message_and_delete_is_local()
    {
        using var temp = new TestRoot();
        var cache = new OfflineMessageCache(temp.Path);
        cache.Upsert(Mail());
        cache.Upsert(Mail(id: "other"));
        cache.Upsert(Mail(revision: "r2") with { Body = "Updated body" });
        Assert.Equal(2, cache.List(MailProvider.Microsoft, "account-A").Count);
        Assert.Equal("r2", cache.Get(MailProvider.Microsoft, "account-A", "message-A")!.Revision);
        Assert.False(cache.Delete(MailProvider.Gmail, "account-A", "message-A"));
        Assert.True(cache.Delete(MailProvider.Microsoft, "account-A", "message-A"));
        Assert.Null(new OfflineMessageCache(temp.Path).Get(MailProvider.Microsoft, "account-A", "message-A"));
        Assert.False(cache.Delete(MailProvider.Microsoft, "account-A", "message-A"));
        Assert.Single(cache.List(MailProvider.Microsoft, "account-A"));
    }

    [Fact]
    public void Invalid_identity_content_timestamp_and_size_are_rejected_without_temp_files()
    {
        using var temp = new TestRoot();
        var cache = new OfflineMessageCache(temp.Path);
        Assert.Throws<ArgumentException>(() => cache.Upsert(Mail() with { Provider = MailProvider.Pst }));
        Assert.Throws<ArgumentException>(() => cache.Upsert(Mail(account: " ")));
        Assert.Throws<ArgumentException>(() => cache.Upsert(Mail() with { FolderId = "" }));
        Assert.Throws<ArgumentException>(() => cache.Upsert(Mail() with { Revision = "" }));
        Assert.Throws<ArgumentException>(() => cache.Upsert(Mail() with { ReceivedUtc = DateTimeOffset.Now.ToOffset(TimeSpan.FromHours(1)) }));
        Assert.Throws<ArgumentException>(() => cache.Upsert(Mail() with { Body = new string('x', 256 * 1024 + 1) }));
        Assert.Throws<ArgumentException>(() => cache.Get(MailProvider.Microsoft, "account-A", ""));
        // IDs are opaque even when they resemble traversal: never embedded in a file name.
        cache.Upsert(Mail(account: "../../untrusted", id: "../message"));
        Assert.NotNull(cache.Get(MailProvider.Microsoft, "../../untrusted", "../message"));
        Assert.All(Directory.EnumerateFiles(temp.Path), path => Assert.DoesNotContain("untrusted", path));
        AssertNoTemps(temp.Path);
    }

    [Fact]
    public void Corrupt_and_oversize_snapshots_fail_closed_without_replacement()
    {
        using var temp = new TestRoot();
        var cache = new OfflineMessageCache(temp.Path);
        cache.Upsert(Mail());
        var file = Directory.GetFiles(temp.Path, "*.json").Single();
        File.WriteAllText(file, "{broken");
        Assert.Throws<InvalidDataException>(() => cache.List(MailProvider.Microsoft, "account-A"));
        Assert.Throws<InvalidDataException>(() => cache.Upsert(Mail(revision: "r2")));
        Assert.Equal("{broken", File.ReadAllText(file));
        AssertNoTemps(temp.Path);
        File.WriteAllText(file, new string('X', OfflineMessageCache.MaxFileBytes + 1));
        Assert.Throws<InvalidDataException>(() => cache.Get(MailProvider.Microsoft, "account-A", "message-A"));
        Assert.Throws<InvalidDataException>(() => cache.Delete(MailProvider.Microsoft, "account-A", "message-A"));
        AssertNoTemps(temp.Path);
    }

    [Fact]
    public void Oversized_serialized_snapshot_does_not_replace_prior_revision_or_leak_temp()
    {
        using var temp = new TestRoot();
        var cache = new OfflineMessageCache(temp.Path);
        cache.Upsert(Mail());
        var oversized = Mail(revision: "r2") with { Body = new string('a', 256 * 1024) };
        for (int i = 0; i < 15; i++) cache.Upsert(oversized with { MessageId = "more-" + i });
        Assert.Throws<InvalidOperationException>(() => cache.Upsert(oversized with { MessageId = "too-big" }));
        Assert.Null(cache.Get(MailProvider.Microsoft, "account-A", "too-big"));
        Assert.Equal("r1", cache.Get(MailProvider.Microsoft, "account-A", "message-A")!.Revision);
        AssertNoTemps(temp.Path);
    }

    [Fact]
    public void Existing_insecure_directories_and_files_are_never_repaired()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        using var temp = new TestRoot();
        File.SetUnixFileMode(temp.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead);
        Assert.Throws<UnauthorizedAccessException>(() => new OfflineMessageCache(temp.Path).List(MailProvider.Microsoft, "account-A"));
        Assert.Equal(UnixFileMode.GroupRead, File.GetUnixFileMode(temp.Path) & UnixFileMode.GroupRead);
        File.SetUnixFileMode(temp.Path, PrivateDirectory);
        var cache = new OfflineMessageCache(temp.Path);
        cache.Upsert(Mail());
        var snapshot = Directory.GetFiles(temp.Path, "*.json").Single();
        File.SetUnixFileMode(snapshot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        Assert.Throws<UnauthorizedAccessException>(() => cache.List(MailProvider.Microsoft, "account-A"));
        Assert.Equal(UnixFileMode.GroupRead, File.GetUnixFileMode(snapshot) & UnixFileMode.GroupRead);
        AssertNoTemps(temp.Path);
    }

    [Fact]
    public void Newly_created_files_are_private_and_symlinks_are_rejected()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        using var temp = new TestRoot();
        var root = Path.Combine(temp.Path, "private-cache");
        var cache = new OfflineMessageCache(root);
        cache.Upsert(Mail());
        Assert.Equal(PrivateDirectory, File.GetUnixFileMode(root));
        foreach (var file in Directory.GetFiles(root))
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        var snapshot = Directory.GetFiles(root, "*.json").Single();
        File.Delete(snapshot);
        File.CreateSymbolicLink(snapshot, Path.Combine(temp.Path, "missing-target"));
        Assert.Throws<IOException>(() => cache.List(MailProvider.Microsoft, "account-A"));
        AssertNoTemps(root);
        var linkedAncestor = Path.Combine(temp.Path, "linked");
        Directory.CreateSymbolicLink(linkedAncestor, root);
        Assert.Throws<IOException>(() => new OfflineMessageCache(Path.Combine(linkedAncestor, "nested")).List(MailProvider.Microsoft, "account-A"));
    }

    private static void AssertNoTemps(string root) =>
        Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.TopDirectoryOnly));

    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private sealed class TestRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openoutlook-cache-test-" + Guid.NewGuid().ToString("N"));
        public TestRoot()
        {
            if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            Directory.CreateDirectory(Path, PrivateDirectory);
        }
        public void Dispose()
        {
            if ((OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && Directory.Exists(Path)) File.SetUnixFileMode(Path, PrivateDirectory);
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
