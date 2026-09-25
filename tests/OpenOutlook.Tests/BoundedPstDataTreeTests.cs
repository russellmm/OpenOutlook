using System.Reflection;
using PstCore;

namespace OpenOutlook.Tests;

public sealed class BoundedPstDataTreeTests
{
    [Fact]
    public void RejectsDeclaredExternalBlockLargerThanBudgetBeforeAllocation()
    {
        using var source = new SyntheticBlockSource(32_000, new Bid(0x100));
        var error = Assert.Throws<PstException>(() => source.Reader.ReadDataTree(new Bid(0x100), 128, default));
        Assert.Contains("permitted size", error.Message);
    }

    [Fact]
    public void RejectsCumulativeExternalBlocksBeforeAllocatingSecond()
    {
        // A Unicode XBLOCK pointing at two external blocks whose combined BBT sizes exceed the cap.
        byte[] index = new byte[24];
        index[0] = 1;
        index[1] = 1;
        index[2] = 2;
        BitConverter.GetBytes(0x100UL).CopyTo(index, 8);
        BitConverter.GetBytes(0x200UL).CopyTo(index, 16);
        byte[] data = new byte[48];
        index.CopyTo(data, 24);
        using var source = new SyntheticBlockSource(data,
            (new Bid(0x102), 24, index.Length),
            (new Bid(0x100), 0, 4),
            (new Bid(0x200), 50_000, 100));
        var error = Assert.Throws<PstException>(() => source.Reader.ReadDataTree(new Bid(0x102), 50, default));
        Assert.Contains("permitted size", error.Message);
    }

    [Fact]
    public void PreCancelledReadDoesNotTouchMissingBlock()
    {
        using var source = new SyntheticBlockSource(0, new Bid(0x100));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            source.Reader.ReadDataTree(new Bid(0x100), 100, cts.Token));
    }

    [Fact]
    public void RejectsMalformedInternalBlockWithoutLeakingIndexException()
    {
        using var source = new SyntheticBlockSource([1, 1, 255, 255], (new Bid(0x102), 0, 4));
        Assert.Throws<PstException>(() => source.Reader.ReadDataTree(new Bid(0x102), 128, default));
    }

    private sealed class SyntheticBlockSource : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "pst-block-test-" + Guid.NewGuid().ToString("N"));
        private readonly FileStream _stream;
        public Ndb Reader { get; }

        public SyntheticBlockSource(int declaredSize, Bid bid)
            : this([], (bid, 0, declaredSize)) { }

        public SyntheticBlockSource(byte[] data, params (Bid Bid, int Offset, int Size)[] blocks)
        {
            File.WriteAllBytes(_path, data);
            _stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var header = new PstHeader
            {
                Path = _path, Format = PstFormatKind.Unicode, WVer = 23, WVerClient = 0,
                CryptMethod = PstCryptMethod.None, FileEof = (ulong)data.Length,
                NbtRootBid = 0, NbtRootIb = 0, BbtRootBid = 0, BbtRootIb = 0,
                AMapValid = 0, Unique = 0
            };
            Reader = (Ndb)Activator.CreateInstance(typeof(Ndb),
                BindingFlags.NonPublic | BindingFlags.Instance, null, [_stream, header], null)!;
            var field = typeof(Ndb).GetField("_bbt", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var entries = (Dictionary<ulong, BbtEntry>)field.GetValue(Reader)!;
            foreach (var (bid, offset, size) in blocks)
                entries.Add(bid.LookupKey, new BbtEntry
                {
                    Bid = bid, Offset = (ulong)offset, Size = checked((ushort)size), RefCount = 1
                });
        }

        public void Dispose()
        {
            Reader.Dispose();
            File.Delete(_path);
        }
    }
}
