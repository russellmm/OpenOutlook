using PstCore;

namespace OpenOutlook.Tests;

public sealed class PstReadOnlyTests
{
    [Fact]
    public void Supplied_sample_opens_read_only_and_exposes_root()
    {
        var path = Environment.GetEnvironmentVariable("OPENOUTLOOK_TEST_PST");
        if (string.IsNullOrWhiteSpace(path)) return; // Opt-in: never package private fixtures.
        var sizeBefore = new FileInfo(path).Length;
        using var store = PstStore.Open(path, writable: false);
        Assert.False(store.CanWrite);
        Assert.NotNull(store.Root);
        var folders = store.AllFolders().ToArray();
        Assert.NotEmpty(folders);
        // Exercise a real message read without asserting or logging private content.
        var summary = folders.SelectMany(store.GetMessages).FirstOrDefault();
        if (summary is not null && !summary.Subject.StartsWith("(unreadable", StringComparison.Ordinal))
            Assert.NotNull(store.OpenMessage(summary));
        Assert.Equal(sizeBefore, new FileInfo(path).Length);
    }
}
