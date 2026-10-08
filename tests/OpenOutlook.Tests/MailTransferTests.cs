using OpenOutlook.Desktop;

namespace OpenOutlook.Tests;

public sealed class MailTransferTests
{
    private sealed class FakeSource(Func<TransferItem, byte[]>? read = null, Action<IReadOnlyList<TransferItem>>? remove = null) : ITransferSource
    {
        public List<string> Removed { get; } = [];
        public Task<byte[]> ReadMimeAsync(TransferItem item, CancellationToken ct) => Task.FromResult(read?.Invoke(item) ?? [1, 2, 3]);
        public Task RemoveAsync(IReadOnlyList<TransferItem> items, CancellationToken ct)
        {
            remove?.Invoke(items);
            Removed.AddRange(items.Select(i => i.Id));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSink(Action<TransferItem>? add = null, Action? commit = null) : ITransferSink
    {
        public List<string> Added { get; } = [];
        public List<string> Committed { get; } = [];
        public int Commits { get; private set; }
        public Task AddAsync(byte[] mime, TransferItem item, CancellationToken ct) { add?.Invoke(item); Added.Add(item.Id); return Task.CompletedTask; }
        public Task CommitAsync(CancellationToken ct)
        {
            commit?.Invoke();
            Commits++;
            Committed.AddRange(Added.Except(Committed));
            return Task.CompletedTask;
        }
    }

    private static List<TransferItem> Items(int n) => Enumerable.Range(1, n).Select(i => new TransferItem("m" + i, "Subject " + i, i % 2 == 0)).ToList();

    [Fact]
    public async Task A_copy_writes_everything_and_removes_nothing()
    {
        var source = new FakeSource();
        var sink = new FakeSink();
        var result = await MailTransfer.RunAsync(source, Items(45), sink, move: false);
        Assert.Equal(45, result.Transferred);
        Assert.Equal(0, result.Removed);
        Assert.True(result.AllDone);
        Assert.Empty(source.Removed);
        Assert.Equal(3, sink.Commits);                        // chunks of 20, 20, 5
    }

    [Fact]
    public async Task A_move_removes_each_original_only_after_its_chunk_is_committed()
    {
        var order = new List<string>();
        var sink = new FakeSink(commit: () => order.Add("commit"));
        var source = new FakeSource(remove: items => order.Add("remove" + items.Count));
        var result = await MailTransfer.RunAsync(source, Items(25), sink, move: true);
        Assert.Equal((25, 25), (result.Transferred, result.Removed));
        Assert.Equal(["commit", "remove20", "commit", "remove5"], order);
        Assert.Equal(Items(25).Select(i => i.Id), source.Removed);
    }

    [Fact]
    public async Task A_message_that_cannot_be_read_or_written_stays_where_it_is_and_the_rest_continue()
    {
        var source = new FakeSource(read: i => i.Id == "m2" ? throw new InvalidOperationException("embedded item") : [1]);
        var sink = new FakeSink(add: i => { if (i.Id == "m3") throw new IOException("rejected"); });
        var result = await MailTransfer.RunAsync(source, Items(4), sink, move: true);
        Assert.Equal(2, result.Transferred);
        Assert.Equal(["m1", "m4"], source.Removed);
        Assert.Equal(["m2", "m3"], result.Failures.Select(f => f.Item.Id).ToArray());
        Assert.Equal("embedded item", result.Failures[0].Error);
        Assert.False(result.AllDone);
    }

    [Fact]
    public async Task A_failed_commit_removes_nothing_of_that_chunk_and_counts_it_as_failed()
    {
        var calls = 0;
        var sink = new FakeSink(commit: () => { if (++calls == 1) throw new IOException("disk full"); });
        var source = new FakeSource();
        var result = await MailTransfer.RunAsync(source, Items(25), sink, move: true);
        Assert.Equal(5, result.Transferred);                  // the second chunk
        Assert.Equal(Items(25).Skip(20).Select(i => i.Id), source.Removed);
        Assert.Equal(20, result.Failures.Count);
        Assert.All(result.Failures, f => Assert.Equal("disk full", f.Error));
    }

    [Fact]
    public async Task A_failed_removal_is_reported_and_stops_the_move_so_duplicates_do_not_pile_up()
    {
        var removals = 0;
        var source = new FakeSource(remove: _ => { if (++removals == 1) throw new IOException("mailbox busy"); });
        var sink = new FakeSink();
        var result = await MailTransfer.RunAsync(source, Items(45), sink, move: true);
        Assert.Equal(20, result.Transferred);                 // first chunk copied, then stopped
        Assert.Equal(0, result.Removed);
        Assert.Equal("mailbox busy", result.RemoveError);
        Assert.False(result.AllDone);
    }

    [Fact]
    public async Task Cancelling_between_messages_stops_without_removing_uncommitted_originals()
    {
        using var cts = new CancellationTokenSource();
        var sink = new FakeSink(add: i => { if (i.Id == "m3") cts.Cancel(); });
        var source = new FakeSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => MailTransfer.RunAsync(source, Items(10), sink, move: true, ct: cts.Token));
        Assert.Empty(source.Removed);
    }
}
