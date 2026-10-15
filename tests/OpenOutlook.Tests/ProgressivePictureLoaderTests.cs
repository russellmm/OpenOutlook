using OpenOutlook.Desktop;

namespace OpenOutlook.Tests;

/// <summary>
/// The reader's picture scheduling (found 2026-10-15 on an American Express mailing whose tracking server took 8 s per pixel). Slow
/// pictures are TaskCompletionSources the test releases itself, so nothing here depends on how fast the machine is; the only real
/// timer is the short first-layout delay, and every wait has a generous timeout.
/// </summary>
public sealed class ProgressivePictureLoaderTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly byte[] Picture = [1, 2, 3];

    [Fact]
    public async Task Quick_pictures_are_laid_out_while_a_slow_one_is_still_loading()
    {
        var slow = new TaskCompletionSource<byte[]?>();
        var accepted = new List<string>();
        var acceptedAtLayout = -1;
        var layoutCalled = new TaskCompletionSource();
        var run = ProgressivePictureLoader.RunAsync(["a", "b", "slow", "c"],
            (name, _) => name == "slow" ? slow.Task : Task.FromResult<byte[]?>(Picture),
            (name, _) => { lock (accepted) accepted.Add(name); return true; },
            () => { lock (accepted) acceptedAtLayout = accepted.Count; layoutCalled.TrySetResult(); },
            () => true, concurrency: 6, TimeSpan.FromMilliseconds(50), CancellationToken.None);

        await layoutCalled.Task.WaitAsync(Patience);          // laid out although "slow" has not arrived
        Assert.Equal(3, acceptedAtLayout);
        Assert.False(run.IsCompleted);

        slow.SetResult(Picture);
        var result = await run.WaitAsync(Patience);
        Assert.True(result.Completed);
        Assert.True(result.LaidOutEarly);
        Assert.Equal(1, result.ArrivedSinceLastLayout);
        Assert.True(result.NeedsFinalLayout);                  // the slow picture still needs a layout
        Assert.Equal(new[] { "a", "b", "c" }, accepted.Take(3).Order());
        Assert.Equal("slow", accepted[3]);
    }

    [Fact]
    public async Task When_every_picture_is_quick_there_is_one_layout_at_the_end()
    {
        var layouts = 0;
        var result = await ProgressivePictureLoader.RunAsync(["a", "b"],
            (_, _) => Task.FromResult<byte[]?>(Picture), (_, _) => true, () => layouts++,
            () => true, concurrency: 6, TimeSpan.FromSeconds(30), CancellationToken.None).WaitAsync(Patience);
        Assert.Equal(0, layouts);                              // and the 30 s first-layout time was not waited for
        Assert.True(result.Completed);
        Assert.False(result.LaidOutEarly);
        Assert.Equal(2, result.ArrivedSinceLastLayout);
        Assert.True(result.NeedsFinalLayout);
    }

    [Fact]
    public async Task A_slow_picture_that_fails_after_the_early_layout_needs_no_second_layout()
    {
        var slow = new TaskCompletionSource<byte[]?>();
        var layoutCalled = new TaskCompletionSource();
        var run = ProgressivePictureLoader.RunAsync(["a", "slow"],
            (name, _) => name == "slow" ? slow.Task : Task.FromResult<byte[]?>(Picture),
            (_, _) => true, () => layoutCalled.TrySetResult(),
            () => true, concurrency: 6, TimeSpan.FromMilliseconds(50), CancellationToken.None);
        await layoutCalled.Task.WaitAsync(Patience);
        slow.SetException(new HttpRequestException("timed out"));
        var result = await run.WaitAsync(Patience);
        Assert.True(result.Completed);
        Assert.Equal(0, result.ArrivedSinceLastLayout);
        Assert.False(result.NeedsFinalLayout);                 // only the status line changes
    }

    [Fact]
    public async Task No_more_than_the_concurrency_limit_load_at_once()
    {
        var gates = Enumerable.Range(0, 20).Select(_ => new TaskCompletionSource<byte[]?>()).ToArray();
        var running = 0;
        var most = 0;
        var started = new SemaphoreSlim(0);
        var run = ProgressivePictureLoader.RunAsync(Enumerable.Range(0, 20).ToArray(),
            async (i, _) =>
            {
                var now = Interlocked.Increment(ref running);
                lock (gates) most = Math.Max(most, now);
                started.Release();
                try { return await gates[i].Task; }
                finally { Interlocked.Decrement(ref running); }
            },
            (_, _) => true, () => { }, () => true, concurrency: 6, TimeSpan.FromMilliseconds(20), CancellationToken.None);

        for (var i = 0; i < 20; i++)
        {
            Assert.True(await started.WaitAsync(Patience), $"fetch {i} never started");
            Assert.True(Volatile.Read(ref running) <= 6);
            if (i >= 5) gates[i - 5].SetResult(Picture);      // keep six in flight, finish the oldest
        }
        for (var i = 15; i < 20; i++) gates[i].SetResult(Picture);
        var result = await run.WaitAsync(Patience);
        Assert.Equal(6, most);
        Assert.True(result.Completed);
    }

    [Fact]
    public async Task Failed_and_refused_pictures_are_not_counted_and_do_not_stop_the_rest()
    {
        var accepted = new List<string>();
        var result = await ProgressivePictureLoader.RunAsync(["ok", "throws", "missing", "over-budget", "ok2"],
            (name, _) => name switch
            {
                "throws" => Task.FromException<byte[]?>(new InvalidDataException("damaged")),
                "missing" => Task.FromResult<byte[]?>(null),
                _ => Task.FromResult<byte[]?>(Picture)
            },
            (name, _) => { if (name == "over-budget") return false; accepted.Add(name); return true; },
            () => { }, () => true, concurrency: 2, TimeSpan.FromSeconds(30), CancellationToken.None).WaitAsync(Patience);
        Assert.Equal(new[] { "ok", "ok2" }, accepted.Order());
        Assert.Equal(2, result.ArrivedSinceLastLayout);
        Assert.True(result.Completed);
    }

    [Fact]
    public async Task Switching_to_another_message_stops_without_a_layout()
    {
        var current = true;
        var slow = new TaskCompletionSource<byte[]?>();
        var layouts = 0;
        var run = ProgressivePictureLoader.RunAsync(["slow"], (_, _) => slow.Task, (_, _) => true, () => layouts++,
            () => current, concurrency: 6, TimeSpan.FromSeconds(30), CancellationToken.None);
        current = false;                                       // the reader moved on to another message
        slow.SetResult(Picture);
        var result = await run.WaitAsync(Patience);
        Assert.False(result.Completed);
        Assert.False(result.NeedsFinalLayout);
        Assert.Equal(0, layouts);
    }

    [Fact]
    public async Task Cancelling_stops_the_load_and_reports_it_unfinished()
    {
        using var cancel = new CancellationTokenSource();
        var hang = new TaskCompletionSource<byte[]?>();
        var run = ProgressivePictureLoader.RunAsync(["a", "b"],
            (_, token) => hang.Task.WaitAsync(token), (_, _) => true, () => { },
            () => true, concurrency: 6, TimeSpan.FromSeconds(30), cancel.Token);
        cancel.Cancel();
        var result = await run.WaitAsync(Patience);
        Assert.False(result.Completed);
        Assert.False(result.NeedsFinalLayout);
    }
}
