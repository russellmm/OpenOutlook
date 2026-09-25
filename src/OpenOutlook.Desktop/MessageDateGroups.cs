using System.Globalization;

namespace OpenOutlook.Desktop;

public static class MessageDateGroups
{
    public static string Label(DateTime received, DateTime today)
    {
        if (received == DateTime.MinValue) return "Undated";
        var date = received.Date;
        today = today.Date;
        if (date > today) return "Future";
        if (date == today) return "Today";
        if (date == today.AddDays(-1)) return "Yesterday";

        var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        var weekStart = today.AddDays(-daysSinceMonday);
        if (date >= weekStart)
            return date.ToString("dddd", CultureInfo.InvariantCulture);
        if (date >= weekStart.AddDays(-7)) return "Last Week";
        if (date.Year == today.Year && date.Month == today.Month) return "Earlier This Month";
        var previousMonth = today.AddMonths(-1);
        if (date.Year == previousMonth.Year && date.Month == previousMonth.Month) return "Last Month";
        return "Older";
    }
}
