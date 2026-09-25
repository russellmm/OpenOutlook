using OpenOutlook.Desktop;

namespace OpenOutlook.Tests;

public sealed class MessageDateGroupsTests
{
    [Theory]
    [InlineData(2026, 9, 23, "Today")]
    [InlineData(2026, 9, 22, "Yesterday")]
    [InlineData(2026, 9, 21, "Monday")]
    [InlineData(2026, 9, 18, "Last Week")]
    [InlineData(2026, 9, 8, "Earlier This Month")]
    [InlineData(2026, 8, 26, "Last Month")]
    [InlineData(2026, 7, 31, "Older")]
    [InlineData(2026, 9, 24, "Future")]
    public void Uses_calendar_boundaries(int year, int month, int day, string expected)
    {
        var today = new DateTime(2026, 9, 23);
        Assert.Equal(expected, MessageDateGroups.Label(new DateTime(year, month, day), today));
    }

    [Fact]
    public void Missing_message_date_is_kept_in_an_undated_group()
    {
        Assert.Equal("Undated", MessageDateGroups.Label(DateTime.MinValue, new DateTime(2026, 9, 23)));
    }
}
