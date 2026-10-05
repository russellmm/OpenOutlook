using OpenOutlook.Desktop;
using Xunit;

namespace OpenOutlook.Tests;

public sealed class SyncIndicatorTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Nothing_to_show_hides_the_indicator()
    {
        Assert.False(SyncIndicatorState.Compute([], Now).Visible);
        Assert.False(SyncIndicatorState.Compute([new MirrorInfo("a@x", SyncPhase.Off, "", null)], Now).Visible);
    }

    [Fact]
    public void Up_to_date_shows_how_long_ago()
    {
        var s = SyncIndicatorState.Compute([new MirrorInfo("a@x", SyncPhase.Idle, "", Now.AddMinutes(-3)), new MirrorInfo("b@x", SyncPhase.Idle, "", Now.AddHours(-2))], Now);
        Assert.Equal(SyncPhase.Idle, s.Phase);
        Assert.Equal("✓", s.Glyph);
        Assert.Equal("Up to date · 3 min ago", s.Text);                       // the newest sync
        Assert.Contains("a@x: up to date, synchronised 3 min ago", s.Tooltip);
        Assert.Contains("b@x: up to date, synchronised 2 h ago", s.Tooltip);
    }

    [Fact]
    public void The_worst_state_wins_and_offline_counts_waiting_changes()
    {
        var idle = new MirrorInfo("a@x", SyncPhase.Idle, "", Now);
        var syncing = new MirrorInfo("b@x", SyncPhase.Syncing, "Inbox 3 of 9", Now);
        var offline = new MirrorInfo("c@x", SyncPhase.Offline, "", Now, 2);
        var problem = new MirrorInfo("d@x", SyncPhase.Problem, "sign in again from Account Settings", Now);
        Assert.Equal("Syncing…", SyncIndicatorState.Compute([idle, syncing], Now).Text);
        var off = SyncIndicatorState.Compute([idle, syncing, offline], Now);
        Assert.Equal("Offline · 2 changes waiting", off.Text);
        Assert.Equal("⚠", off.Glyph);
        var bad = SyncIndicatorState.Compute([idle, syncing, offline, problem], Now);
        Assert.Equal("Sync problem", bad.Text);
        Assert.Contains("d@x: sign in again from Account Settings", bad.Tooltip);
        Assert.Contains("b@x: syncing (Inbox 3 of 9)", bad.Tooltip);
    }

    [Fact]
    public void A_copy_that_has_not_synced_yet_says_so()
    {
        var s = SyncIndicatorState.Compute([new MirrorInfo("a@x", SyncPhase.Idle, "", null)], Now);
        Assert.Equal("Waiting for the first sync", s.Text);
    }
}
