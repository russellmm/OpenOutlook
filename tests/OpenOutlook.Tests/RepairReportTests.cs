using OpenOutlook.Desktop;
using PstCore;

namespace OpenOutlook.Tests;

/// <summary>The Check and Repair window's report (2026-10-16: it said "11 problem lines found" for one problem).</summary>
public sealed class RepairReportTests
{
    // the checker's text as the owner saw it for rmarrash_3.pst: one indented problem, ten per-check summary lines
    private static readonly string[] OwnersReport =
    [
        "refs: 471654 blocks, 0 orphans, 0 refcount mismatches, 0 missing blocks",
        "nids: 0 problems",
        "tables: 13410 messages listed in contents tables, 0 message nodes not listed, 0 problems",
        "idmap: 13454 records (13453 live), 13455 rows checked (2 without an ID, informational), 0 problems",
        "xblocks: 57843 data-tree blocks checked, 0 problems",
        "rowvers: 13455 rows with a version, 0 problems",
        "subnodes: 13410 messages with subnodes, 2638 attachments, 0 problems",
        "  contents table 0x818e: 2 of 515 rows have a conversation id (0x3013) other than the one Outlook writes",
        "rowcells: 13410 message rows, 1 problems",
        "amap: free 273616640 (header 273616640), 0 blocks not allocated, 0 problems",
        "folders: 0 problems",
    ];

    private static readonly ArchiveRepairWindow.DataFile File = new(@"F:\Claude\Mail-data\rmarrash_3.pst", "rmarrash_hotmail", CanRepair: true);

    [Fact]
    public void Only_the_indented_lines_count_as_problems_and_they_come_first()
    {
        var text = ArchiveRepairWindow.Summarize(new PstScanReport(OwnersReport, 2, 0), repaired: false, File);
        var lines = text.Split(Environment.NewLine);
        Assert.Contains("1 problem found; 2 items can be repaired automatically.", lines);
        Assert.DoesNotContain(lines, l => l.Contains("problem lines", StringComparison.Ordinal));
        var problem = Array.FindIndex(lines, l => l.StartsWith("- contents table 0x818e", StringComparison.Ordinal));
        var checks = Array.IndexOf(lines, "Checks run:");
        Assert.True(problem > 0 && checks > problem, text);                       // the problem first, then what was checked
        Assert.Contains("folders: 0 problems", lines);
    }

    [Fact]
    public void A_clean_report_and_a_report_without_the_indented_format_still_read_right()
    {
        Assert.Contains("No problems found.", ArchiveRepairWindow.Summarize(new PstScanReport([], 0, 0), false, File));
        var plain = ArchiveRepairWindow.Summarize(new PstScanReport(["something is wrong", "and this too"], 0, 0), false, File);
        Assert.Contains("2 problems found; nothing can be repaired automatically.", plain);
        Assert.Contains("Repaired 2 items.", ArchiveRepairWindow.Summarize(new PstScanReport([], 0, 2), true, File));
    }
}
