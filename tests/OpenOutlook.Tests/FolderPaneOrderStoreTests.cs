using OpenOutlook.Desktop;
using System.Text.Json;
using Xunit;

namespace OpenOutlook.Tests;

public sealed class FolderPaneOrderStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oo-fold-order-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private FolderPaneOrderStore Store() => new(Path.Combine(_root, "config"));

    [Fact]
    public void MissingFileLoadsEmpty()
    {
        Assert.Empty(Store().Load());
    }

    [Fact]
    public void RoundTripsOrders()
    {
        var store = Store();
        store.Save(new Dictionary<string, List<string>>
        {
            [""] = new List<string> { "pst:/a.pst", "acct:42" },
            ["pst:/a.pst"] = new List<string> { "psf:/a.pst|00000102", "psf:/a.pst|00000203" }
        });
        var loaded = store.Load();
        Assert.Equal(new[] { "pst:/a.pst", "acct:42" }, loaded[""]);
        Assert.Equal(new[] { "psf:/a.pst|00000102", "psf:/a.pst|00000203" }, loaded["pst:/a.pst"]);
    }

    [Fact]
    public void RootOrderUnderTheEmptyParentKeyPersists()
    {
        // The tree's own root is the empty-string parent; rejecting it silently disabled root reordering.
        var store = Store();
        store.Save(new Dictionary<string, List<string>> { [""] = new List<string> { "acct:1", "pst:/b.pst" } });
        Assert.Equal(new[] { "acct:1", "pst:/b.pst" }, store.Load()[""]);
    }

    [Fact]
    public void SaveOverwritesAtomically()
    {
        var store = Store();
        store.Save(new Dictionary<string, List<string>> { [""] = new List<string> { "x", "y" } });
        store.Save(new Dictionary<string, List<string>> { [""] = new List<string> { "y", "x" } });
        Assert.Equal(new[] { "y", "x" }, store.Load()[""]);
        // No temp files left behind.
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store.Path)!, ".*.tmp"));
    }

    [Fact]
    public void CorruptFileLoadsEmpty()
    {
        var store = Store();
        Directory.CreateDirectory(Path.GetDirectoryName(store.Path)!);
        File.WriteAllText(store.Path, "{ not json at all");
        Assert.Empty(store.Load());
    }

    [Fact]
    public void OversizedFileLoadsEmpty()
    {
        var store = Store();
        Directory.CreateDirectory(Path.GetDirectoryName(store.Path)!);
        var pad = new string('x', FolderPaneOrderStore.MaximumFileBytes + 16);
        File.WriteAllText(store.Path, JsonSerializer.Serialize(new Dictionary<string, List<string>> { ["p"] = new() { pad } }));
        Assert.Empty(store.Load());
    }

    [Fact]
    public void ControlCharactersAndEmptyKeysAreDropped()
    {
        var store = Store();
        Directory.CreateDirectory(Path.GetDirectoryName(store.Path)!);
        File.WriteAllText(store.Path, JsonSerializer.Serialize(new
        {
            orders = new Dictionary<string, List<string>>
            {
                ["good"] = new() { "keep", "", "bad\nkey" },
                ["line\nbreak"] = new() { "x" }
            }
        }));
        var loaded = store.Load();
        Assert.Equal(new[] { "keep" }, loaded["good"]);
        Assert.DoesNotContain("line\nbreak", loaded.Keys);
    }

    [Fact]
    public void UnknownKeysRankLast()
    {
        Assert.Equal(int.MaxValue, FolderPaneOrderStore.RankOf(null, "anything"));
        Assert.Equal(int.MaxValue, FolderPaneOrderStore.RankOf(new[] { "a" }, null));
        Assert.Equal(int.MaxValue, FolderPaneOrderStore.RankOf(new[] { "a", "b" }, "zzz"));
        Assert.Equal(1, FolderPaneOrderStore.RankOf(new[] { "a", "b" }, "b"));
    }

    [Fact]
    public void SortKeysAppliesSavedOrderAndKeepsUnknownsStableAtTheEnd()
    {
        var sorted = FolderPaneOrderStore.SortKeys(
            new[] { "a", "new1", "b", "c", "new2" },
            new[] { "c", "a" });
        Assert.Equal(new[] { "c", "a", "new1", "b", "new2" }, sorted);
    }

    [Fact]
    public void SortKeysWithoutSavedOrderIsIdentity()
    {
        var keys = new[] { "z", "y", "x" };
        Assert.Equal(keys, FolderPaneOrderStore.SortKeys(keys, null));
    }
}
