using OpenOutlook.Desktop;
using PstCore;
using Xunit;

namespace OpenOutlook.Tests;

public sealed class ReadStateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oo-readstate-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private ReadStateStore Store() => new(Path.Combine(_root, "config"));

    [Fact]
    public void MissingFileLoadsEmptyMap() => Assert.Empty(Store().Load());

    [Fact]
    public void RoundTripsOverrides()
    {
        var store = Store();
        store.Save(new Dictionary<string, bool>
        {
            ["pst:/mail/archive.pst|00001234"] = true,
            ["msg:acct-1/AAMKAGH"] = false
        });
        var loaded = store.Load();
        Assert.True(loaded["pst:/mail/archive.pst|00001234"]);
        Assert.False(loaded["msg:acct-1/AAMKAGH"]);
    }

    [Fact]
    public void AtomicOverwriteLeavesNoTempFiles()
    {
        var store = Store();
        store.Save(new Dictionary<string, bool> { ["a"] = true });
        store.Save(new Dictionary<string, bool> { ["b"] = false });
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store.Path)!, ".*.tmp"));
        var loaded = store.Load();
        Assert.False(loaded.ContainsKey("a"));
        Assert.False(loaded["b"]);
    }

    [Fact]
    public void CorruptFileYieldsEmptyMap()
    {
        var store = Store();
        Directory.CreateDirectory(Path.GetDirectoryName(store.Path)!);
        File.WriteAllText(store.Path, "{ not json");
        Assert.Empty(store.Load());
    }

    [Fact]
    public void OversizedFileYieldsEmptyMap()
    {
        var store = Store();
        Directory.CreateDirectory(Path.GetDirectoryName(store.Path)!);
        File.WriteAllText(store.Path, new string('x', ReadStateStore.MaximumFileBytes + 1));
        Assert.Empty(store.Load());
    }

    [Fact]
    public void JunkKeysAreDroppedOnLoadAndSave()
    {
        var store = Store();
        Directory.CreateDirectory(Path.GetDirectoryName(store.Path)!);
        File.WriteAllText(store.Path,
            "{\"overrides\":{\"" + new string('k', ReadStateStore.MaximumKeyLength + 10) + "\":true,\"bad\\u0001key\":true,\"good\":true}}");
        var loaded = store.Load();
        Assert.Equal(new[] { "good" }, loaded.Keys);

        store.Save(new Dictionary<string, bool>
        {
            [""] = true,
            ["line\nbreak"] = true,
            [new string('k', ReadStateStore.MaximumKeyLength)] = false,
            ["kept"] = true
        });
        var saved = store.Load();
        Assert.Equal(2, saved.Count);
        Assert.True(saved.ContainsKey(new string('k', ReadStateStore.MaximumKeyLength)));
        Assert.True(saved["kept"]);
    }

    [Fact]
    public void PstAndGraphKeysFollowTheFolderOrderScheme()
    {
        Assert.Equal("pst:/mail/a.pst|00AB12CD", MainWindow.PstMessageKey("/mail/a.pst", 0x00AB12CD));
        Assert.Equal("msg:acct/AAMK", MainWindow.GraphMessageKey("acct", "AAMK"));
    }
}

public sealed class MessageListRowTests
{
    private static MailSummary Summary(bool read) => new() { Nid = 7, FolderNid = 1, Subject = "s", IsRead = read };

    [Fact]
    public void UnreadRowsRenderBoldAndFlipWithNotification()
    {
        var row = new MessageListRow(Summary(read: false));
        Assert.True(row.IsUnread);
        Assert.Equal(Avalonia.Media.FontWeight.Bold, row.TextWeight);
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        row.SetRead(true);
        Assert.False(row.IsUnread);
        Assert.Equal(Avalonia.Media.FontWeight.Normal, row.TextWeight);
        Assert.True(row.Summary.IsRead);
        Assert.Contains(nameof(MessageListRow.IsUnread), raised);
        Assert.Contains(nameof(MessageListRow.TextWeight), raised);
    }

    [Fact]
    public void SettingTheSameStateRaisesNothing()
    {
        var row = new MessageListRow(Summary(read: true));
        var count = 0;
        row.PropertyChanged += (_, _) => count++;
        row.SetRead(true);
        Assert.Equal(0, count);
    }
}
