using System.Globalization;
using System.Net.Mail;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Desktop;

/// <summary>Creates a personal appointment or a meeting on the selected Microsoft calendar.</summary>
public sealed class CalendarEditorWindow : Window
{
    private readonly TextBox _subject = new() { Watermark = "Subject" };
    private readonly TextBox _date = new() { Watermark = "YYYY-MM-DD" };
    private readonly TextBox _start = new() { Watermark = "HH:mm" };
    private readonly TextBox _endDate = new() { Watermark = "YYYY-MM-DD" };
    private readonly TextBox _end = new() { Watermark = "HH:mm" };
    private readonly TextBox _location = new() { Watermark = "Location (optional)" };
    private readonly TextBox _body = new() { Watermark = "Notes (optional)", AcceptsReturn = true, Height = 100,
        TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _attendees = new() { Watermark = "name@example.com; another@example.com" };
    private readonly CheckBox _allDay = new() { Content = "All day" };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Firebrick };

    public CalendarEditorWindow(DateOnly day, bool meeting)
    {
        Title = meeting ? "New meeting — OpenOutlook" : "New appointment — OpenOutlook";
        Width = 530;
        Height = meeting ? 590 : 545;
        MinWidth = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _date.Text = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        _endDate.Text = _date.Text;
        _start.Text = "09:00";
        _end.Text = "10:00";
        _allDay.IsCheckedChanged += (_, _) =>
        {
            _start.IsEnabled = _end.IsEnabled = _allDay.IsChecked != true;
        };

        var form = new StackPanel { Spacing = 8, Margin = new Thickness(20) };
        form.Children.Add(new TextBlock { Text = meeting ? "New meeting" : "New appointment",
            FontSize = 21, FontWeight = FontWeight.SemiBold });
        form.Children.Add(new TextBlock { Text = "Subject" });
        form.Children.Add(_subject);
        var dates = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,*,2*,*") };
        AddField(dates, "Start date", _date, 0);
        AddField(dates, "Time", _start, 1);
        AddField(dates, "End date", _endDate, 2);
        AddField(dates, "Time", _end, 3);
        form.Children.Add(dates);
        form.Children.Add(_allDay);
        form.Children.Add(new TextBlock { Text = "Time zone: " + TimeZoneInfo.Local.DisplayName,
            FontSize = 12 });
        form.Children.Add(new TextBlock { Text = "Location" });
        form.Children.Add(_location);
        if (meeting)
        {
            form.Children.Add(new TextBlock { Text = "Attendees (separate addresses with semicolons)" });
            form.Children.Add(_attendees);
        }
        form.Children.Add(new TextBlock { Text = "Notes" });
        form.Children.Add(_body);
        form.Children.Add(_error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => Close(null);
        var save = new Button { Content = meeting ? "Save and invite" : "Save appointment" };
        save.Click += (_, _) => Save(meeting);
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        form.Children.Add(buttons);
        Content = new ScrollViewer { Content = form };
        Opened += (_, _) => _subject.Focus();
    }

    private static void AddField(Grid grid, string caption, Control field, int column)
    {
        var panel = new StackPanel { Spacing = 5, Margin = new Thickness(column == 0 ? 0 : 8, 0, 0, 0) };
        panel.Children.Add(new TextBlock { Text = caption });
        panel.Children.Add(field);
        Grid.SetColumn(panel, column);
        grid.Children.Add(panel);
    }

    private void Save(bool meeting)
    {
        var subject = _subject.Text?.Trim() ?? "";
        if (subject.Length is < 1 or > 4096) { _error.Text = "Enter a subject."; return; }
        if (!DateOnly.TryParseExact(_date.Text, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var first) ||
            !DateOnly.TryParseExact(_endDate.Text, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var last))
        { _error.Text = "Enter dates as YYYY-MM-DD."; return; }
        var allDay = _allDay.IsChecked == true;
        var startTime = TimeOnly.MinValue;
        var endTime = TimeOnly.MinValue;
        if (!allDay && (!TimeOnly.TryParseExact(_start.Text, "HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out startTime) ||
            !TimeOnly.TryParseExact(_end.Text, "HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out endTime)))
        { _error.Text = "Enter times as HH:mm (24-hour clock)."; return; }
        if (allDay) last = last.AddDays(1); // Graph uses an exclusive midnight end for all-day events.
        var localStart = first.ToDateTime(startTime, DateTimeKind.Unspecified);
        var localEnd = last.ToDateTime(endTime, DateTimeKind.Unspecified);
        if (TimeZoneInfo.Local.IsInvalidTime(localStart) || TimeZoneInfo.Local.IsInvalidTime(localEnd))
        { _error.Text = "That time does not exist in your local time zone. Choose another time."; return; }
        if (TimeZoneInfo.Local.IsAmbiguousTime(localStart) || TimeZoneInfo.Local.IsAmbiguousTime(localEnd))
        { _error.Text = "That time occurs twice in your local time zone. Choose another time."; return; }
        var start = new DateTimeOffset(localStart, TimeZoneInfo.Local.GetUtcOffset(localStart));
        var end = new DateTimeOffset(localEnd, TimeZoneInfo.Local.GetUtcOffset(localEnd));
        if (end <= start || end - start > TimeSpan.FromDays(366))
        { _error.Text = "The end must be after the start."; return; }
        var zone = TimeZoneInfo.Local.Id;
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(zone, out var windowsZone)) zone = windowsZone;
        var attendees = meeting ? (_attendees.Text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries) : [];
        if (meeting && attendees.Length == 0)
        { _error.Text = "Add at least one attendee for a meeting."; return; }
        if (attendees.Length > 100 || attendees.Any(address =>
                !MailAddress.TryCreate(address, out var parsed) || parsed.Address != address))
        { _error.Text = "Enter valid attendee email addresses separated by semicolons."; return; }
        Close(new NewCalendarEvent(subject, start, end, allDay, _location.Text ?? "", _body.Text ?? "",
            attendees, Guid.NewGuid(), zone));
    }
}
