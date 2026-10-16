using OpenOutlook.Desktop;

namespace OpenOutlook.Tests;

/// <summary>The log viewer's reader (File > Account > View log, 2026-10-16).</summary>
public sealed class AppLogReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oo-logview-" + Guid.NewGuid().ToString("N"));
    private string LogPath => Path.Combine(_dir, "openoutlook.log");

    public AppLogReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    [Fact]
    public void Entries_come_newest_first_with_their_stack_traces_and_the_rotated_file_before_them()
    {
        File.WriteAllLines(LogPath + ".1", ["2026-10-15 09:00:00 [startup] OpenOutlook 0.1.40 starting"]);
        File.WriteAllLines(LogPath,
        [
            "2026-10-16 12:00:00 [startup] OpenOutlook 0.1.48 starting",
            "2026-10-16 12:00:01 [pst-open] could not open F:\\x.pst: PstException: cannot open",
            "   at OpenOutlook.PstNative.NativePstEngine.Open(String path)",
            "   at OpenOutlook.Desktop.MainWindow.OpenArchiveAsync(String path)",
            "2026-10-16 12:00:05 [reader] picture not loaded, gone (https://cdn.example/fb.png, img ?x?): RemoteImageRefusedException",
        ]);
        var r = AppLogReader.Read(LogPath);
        Assert.Equal(4, r.Total);
        Assert.StartsWith("2026-10-16 12:00:05 [reader]", r.Entries[0], StringComparison.Ordinal);
        Assert.Contains("OpenArchiveAsync", r.Entries[1]);                          // the stack trace stays with its entry
        Assert.StartsWith("2026-10-15 09:00:00", r.Entries[^1], StringComparison.Ordinal);   // the rotated file is older
    }

    [Fact]
    public void Search_and_only_problems_filter_whole_entries()
    {
        File.WriteAllLines(LogPath,
        [
            "2026-10-16 12:00:00 [startup] OpenOutlook 0.1.48 starting",
            "2026-10-16 12:00:01 [pst-engine] rmarrash_3.pst opened, editable",
            "2026-10-16 12:00:02 [reader] picture not loaded, blocked (https://p.liadm.com/x): HttpRequestException",
            "2026-10-16 12:00:03 [mirror] sync failed",
            "   at Something.Deep(OpenArchiveAsync)",
        ]);
        Assert.Equal(["2026-10-16 12:00:01 [pst-engine] rmarrash_3.pst opened, editable"], AppLogReader.Read(LogPath, "RMARRASH").Entries);
        Assert.Single(AppLogReader.Read(LogPath, "OpenArchiveAsync").Entries);           // matches inside a stack trace
        var problems = AppLogReader.Read(LogPath, problemsOnly: true).Entries;
        Assert.Equal(2, problems.Count);
        Assert.All(problems, e => Assert.DoesNotContain("starting", e));
    }

    [Fact]
    public void A_missing_log_and_a_long_log_are_handled()
    {
        Assert.Empty(AppLogReader.Read(LogPath).Entries);
        File.WriteAllLines(LogPath, Enumerable.Range(0, 5000).Select(i => $"2026-10-16 12:{i / 60 % 60:00}:{i % 60:00} [x] entry {i}"));
        var r = AppLogReader.Read(LogPath, max: 2000);
        Assert.Equal(5000, r.Total);
        Assert.Equal(2000, r.Entries.Count);
        Assert.EndsWith("entry 4999", r.Entries[0], StringComparison.Ordinal);
    }

    [Fact]
    public void The_log_can_be_read_while_the_program_is_writing_to_it()
    {
        File.WriteAllLines(LogPath, ["2026-10-16 12:00:00 [startup] starting"]);
        using var writer = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        Assert.Single(AppLogReader.Read(LogPath).Entries);
    }
}
