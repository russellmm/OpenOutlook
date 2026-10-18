using System.Net.Mail;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Desktop;

/// <summary>
/// The appointment editor, laid out like Outlook's: quick-access buttons, the File / Appointment / Scheduling Assistant / Insert / Format Text / Review /
/// Help ribbon with the "Tell me what you want to do" box, the MailTips banner, Save &amp; Close beside the Title / Start time / End time / Location rows, and
/// the notes body. Invite Attendees turns the appointment into a meeting with a To row. Buttons whose feature is not built yet say "Planned Feature".
/// </summary>
public sealed class CalendarEditorWindow : Window
{
    private const string NotYet = "Planned Feature";

    private readonly TextBox _subject = new() { BorderThickness = new Thickness(0, 0, 0, 1) };
    private readonly TextBox _location = new() { BorderThickness = new Thickness(0, 0, 0, 1) };
    private readonly TextBox _attendees = new() { BorderThickness = new Thickness(0, 0, 0, 1), Watermark = "name@example.com; another@example.com" };
    private readonly TextBox _body = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Watermark = "Add notes for this appointment" };
    private readonly OutlookDateBox _startDate = new();
    private readonly OutlookDateBox _endDate = new();
    private readonly OutlookTimeBox _startTime = new();
    private readonly OutlookTimeBox _endTime = new();
    private readonly ToggleButton _private = new(), _high = new(), _low = new();
    private readonly Button _recurring;
    private TimeSpan _duration = TimeSpan.FromMinutes(30);
    private bool _linking;
    private GraphEventDetails? _existing;
    private bool _readOnly;

    /// <summary>Delete was chosen for an existing event (the window closes with no draft).</summary>
    public bool DeleteRequested { get; private set; }
    /// <summary>For an existing event: whether the notes and the attendees were changed (only those are sent).</summary>
    public bool BodyChanged => _existing is not null && (_body.Text ?? "") != _existing.Body;
    public bool AttendeesChanged => _existing is not null && _existing.IsOrganizer && _isMeeting &&
        !Attendees().Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(_existing.Attendees.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
    public GraphEventDetails? Existing => _existing;
    private readonly CheckBox _allDay = new() { Content = "All day" };
    private readonly CheckBox _timeZones = new();
    private readonly Grid _attendeesRow = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 4), Foreground = Brushes.Gray };
    private readonly ComboBox _showAs = new() { MinWidth = 110 };
    private readonly ComboBox _reminder = new() { MinWidth = 120 };
    private bool _isMeeting;

    public CalendarEditorWindow(DateOnly day, bool meeting, string? attendees = null)
    {
        _attendees.Text = attendees ?? "";
        _isMeeting = meeting;
        Title = meeting ? "Untitled - Meeting" : "Untitled - Appointment";
        Width = 1040;
        Height = 720;
        MinWidth = 780;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _startDate.Date = day; _endDate.Date = day;
        _startTime.Time = new TimeOnly(9, 0); _endTime.Time = new TimeOnly(9, 30);          // Outlook's half-hour default
        _endTime.DurationFrom = () => _startDate.Date == _endDate.Date ? _startTime.Time : null;
        // As in Outlook, moving the start moves the end with it (the length stays); changing the end changes the length.
        _startDate.DateChanged += _ => KeepDuration();
        _startTime.TimeChanged += _ => KeepDuration();
        _endDate.DateChanged += _ => NoteDuration();
        _endTime.TimeChanged += _ => NoteDuration();
        _allDay.IsCheckedChanged += (_, _) =>
        {
            _startTime.IsEnabled = _endTime.IsEnabled = _allDay.IsChecked != true && !_readOnly;
        };
        _recurring = LinkButton("Make Recurring");
        _subject.TextChanged += (_, _) => UpdateTitle();
        ToolTip.SetTip(_timeZones, "Time zone: " + TimeZoneInfo.Local.DisplayName + ". Changing it is a Planned Feature.");

        Content = BuildLayout();
        _attendeesRow.IsVisible = meeting;
        InitOptions();
        Opened += (_, _) => _subject.Focus();
    }

    /// <summary>Opens an existing event (double-click in the calendar): the fields as saved; Save &amp; Close updates it, Delete removes it.</summary>
    public CalendarEditorWindow(GraphEventDetails existing) : this(existing.StartDate, existing.Attendees.Count > 0, string.Join("; ", existing.Attendees))
    {
        _existing = existing;
        _subject.Text = existing.Subject;
        _location.Text = existing.Location;
        _body.Text = existing.Body;
        _linking = true;
        _allDay.IsChecked = existing.IsAllDay;
        if (existing.IsAllDay)
        {
            _startDate.Date = existing.StartDate;
            _endDate.Date = existing.EndDate > existing.StartDate ? existing.EndDate.AddDays(-1) : existing.StartDate;     // Graph's end is the next midnight
        }
        else
        {
            var start = existing.Start.ToLocalTime(); var end = existing.End.ToLocalTime();
            _startDate.Date = DateOnly.FromDateTime(start.DateTime); _startTime.Time = TimeOnly.FromDateTime(start.DateTime);
            _endDate.Date = DateOnly.FromDateTime(end.DateTime); _endTime.Time = TimeOnly.FromDateTime(end.DateTime);
        }
        _linking = false;
        NoteDuration();
        SelectReminder(existing.IsReminderOn ? existing.ReminderMinutesBeforeStart : null);
        _showAs.SelectedItem = _showAs.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == existing.ShowAs) ?? _showAs.SelectedItem;
        _private.IsChecked = existing.Sensitivity == "private";
        _high.IsChecked = existing.Importance == "high";
        _low.IsChecked = existing.Importance == "low";
        _recurring.IsVisible = !existing.IsOccurrence;
        if (!existing.IsOrganizer)
        {
            _readOnly = true;
            foreach (var c in new Control[] { _subject, _location, _attendees, _body, _allDay, _showAs, _reminder, _private, _high, _low }) c.IsEnabled = false;
            _startDate.IsEnabled = _endDate.IsEnabled = _startTime.IsEnabled = _endTime.IsEnabled = false;
            Info($"A meeting organized by {existing.Organizer}: only the organizer can change it. Delete removes it from your calendar.");
        }
        else if (existing.IsOccurrence) Info("One occurrence of a recurring series: changes apply to this occurrence only.");
        UpdateTitle();
    }

    private void KeepDuration()
    {
        if (_linking) return;
        _linking = true;
        var end = _startDate.Date.ToDateTime(_startTime.Time) + _duration;
        _endDate.Date = DateOnly.FromDateTime(end); _endTime.Time = TimeOnly.FromDateTime(end);
        _linking = false;
    }

    private void NoteDuration()
    {
        if (_linking) return;
        var length = _endDate.Date.ToDateTime(_endTime.Time) - _startDate.Date.ToDateTime(_startTime.Time);
        if (length > TimeSpan.Zero) _duration = length;
    }

    /// <summary>Outlook's Show As choices with their colour squares (Graph's showAs values).</summary>
    public static readonly (string Label, string Value, Color Color)[] ShowAsChoices =
    [
        ("Free", "free", Colors.White), ("Working Elsewhere", "workingElsewhere", Color.FromRgb(0xC8, 0xC8, 0xC8)),
        ("Tentative", "tentative", Color.FromRgb(0xA6, 0xC8, 0xF0)), ("Busy", "busy", Color.FromRgb(0x2F, 0x6F, 0xC9)),
        ("Out of Office", "oof", Color.FromRgb(0x80, 0x00, 0x80)),
    ];

    /// <summary>Outlook's Reminder choices (minutes before the start; null = None). 15 minutes is the default, as in Outlook.</summary>
    public static readonly (string Label, int? Minutes)[] ReminderChoices =
    [
        ("None", null), ("0 minutes", 0), ("5 minutes", 5), ("10 minutes", 10), ("15 minutes", 15), ("30 minutes", 30),
        ("1 hour", 60), ("2 hours", 120), ("3 hours", 180), ("4 hours", 240), ("8 hours", 480),
        ("0.5 days", 720), ("1 day", 1440), ("2 days", 2880), ("1 week", 10080),
    ];

    private readonly List<(string Label, int? Minutes)> _reminderChoices = [.. ReminderChoices];

    private void InitOptions()
    {
        foreach (var (label, value, color) in ShowAsChoices)
            _showAs.Items.Add(new ComboBoxItem
            {
                Tag = value,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 6,
                    Children =
                    {
                        new Border { Width = 14, Height = 14, Background = new SolidColorBrush(color), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center },
                    }
                }
            });
        _showAs.SelectedIndex = Array.FindIndex(ShowAsChoices, c => c.Value == "busy");
        ToolTip.SetTip(_showAs, "How this time shows to others who look at your calendar");
        foreach (var (label, _) in _reminderChoices) _reminder.Items.Add(label);
        _reminder.SelectedIndex = _reminderChoices.FindIndex(c => c.Minutes == 15);
        ToolTip.SetTip(_reminder, "When OpenOutlook (and Outlook, and your phone) reminds you of this item");
        _high.IsCheckedChanged += (_, _) => { if (_high.IsChecked == true) _low.IsChecked = false; };
        _low.IsCheckedChanged += (_, _) => { if (_low.IsChecked == true) _high.IsChecked = false; };
    }

    /// <summary>Selects the reminder of an existing event; a time Outlook set that is not in the list (18 hours for an all-day event) is added to it.</summary>
    private void SelectReminder(int? minutes)
    {
        var i = _reminderChoices.FindIndex(c => c.Minutes == minutes);
        if (i < 0 && minutes is { } m)
        {
            var label = m % 1440 == 0 ? $"{m / 1440} days" : m % 60 == 0 ? $"{m / 60} hours" : $"{m} minutes";
            i = _reminderChoices.FindIndex(c => c.Minutes > m);
            if (i < 0) i = _reminderChoices.Count;
            _reminderChoices.Insert(i, (label, m));
            _reminder.Items.Insert(i, label);
        }
        _reminder.SelectedIndex = Math.Max(0, i);
    }

    // ---------------------------------------------------------------- layout

    private Control BuildLayout()
    {
        var root = new DockPanel { Background = Brushes.WhiteSmoke };
        var top = new StackPanel();
        top.Children.Add(QuickAccess());
        top.Children.Add(RibbonHost());
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);
        var center = new DockPanel { Margin = new Thickness(8, 4, 8, 8) };
        var banner = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF4)),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(12, 5),
            Child = new TextBlock { Text = "We can't show MailTips right now.", FontSize = 12, Foreground = Brushes.Gray }
        };
        DockPanel.SetDock(banner, Dock.Top);
        center.Children.Add(banner);
        center.Children.Add(new Border
        {
            Background = Brushes.White,
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = Form()
        });
        root.Children.Add(center);
        return root;
    }

    private Control QuickAccess()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(8, 4, 8, 0) };
        bar.Children.Add(Icon("OlIconAppMark", 22));
        bar.Children.Add(Small("OlIconSave", "Save & Close", () => { Save(); return Task.CompletedTask; }));
        bar.Children.Add(Small("OlIconUndo", "Undo", () => { _body.Undo(); return Task.CompletedTask; }));
        bar.Children.Add(Small("OlIconRedo", "Redo", () => { _body.Redo(); return Task.CompletedTask; }));
        bar.Children.Add(Rotated(Small("OlIconSortUp", "Move up", null), 0));
        bar.Children.Add(Rotated(Small("OlIconSortUp", "Move down", null), 180));
        bar.Children.Add(Small("OlIconFilter", "Filter", null));
        return bar;
    }

    private Control RibbonHost()
    {
        var tabs = Ribbon();
        // Outlook puts the "Tell me what you want to do" box in the tab strip row, to the right of the last tab.
        var grid = new Grid { Margin = new Thickness(4, 0) };
        var tellMe = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 5, 8, 0),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
                Children = { Icon("OlIconBulb", 16), new TextBlock { Text = "Tell me what you want to do", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center } }
            }
        };
        Wire(tellMe, "Tell me", null);
        grid.Children.Add(tabs);
        grid.Children.Add(tellMe);
        return grid;
    }

    private TabControl Ribbon()
    {
        var tabs = new TabControl { Padding = new Thickness(0), Background = Brushes.Transparent };
        if (Application.Current?.Styles is not null) tabs.Classes.Add("olTabs");
        void Tab(string name, params Control[] groups)
        {
            var strip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2), Spacing = 0 };
            foreach (var g in groups) strip.Children.Add(g);
            tabs.Items.Add(new TabItem { Header = name, Content = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = strip } });
        }
        Tab("File", Group("Actions", Big("OlIconSaveClose", "Save & Close", () => { Save(); return Task.CompletedTask; }), Big(null, "Discard", () => { Close(null); return Task.CompletedTask; }), Big("OlIconPrint", "Print", null)));
        Tab("Appointment",
            Group("Actions", Big("OlIconDelete", "Delete", () => { DeleteRequested = _existing is not null; Close(null); return Task.CompletedTask; }),
                Stack(Labelled(Mini("OlIconCalendar", "Calendar", null), "Calendar"), Labelled(Mini("OlIconForward", "Forward", null), "Forward \u02c5"))),
            Group("Attendees", Big("OlIconInvite", "Invite Attendees", () => { ToggleAttendees(); return Task.CompletedTask; })),
            Group("Options", Stack(
                Row(new TextBlock { Text = "Show As:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }, Real(_showAs)),
                Row(Icon("OlIconRulesBell", 16), new TextBlock { Text = "Reminder:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 6, 0) }, Real(_reminder))),
                Big("OlIconRecurrence", "Recurrence", null)),
            Group("Tags", Big("OlIconCategorize", "Categorize \u02c5", null),
                Stack(Toggle(_private, "OlIconLock", "Private"), Toggle(_high, "OlIconImportanceHigh", "High Importance"), Toggle(_low, "OlIconImportanceLow", "Low Importance"))),
            Group("Immersive", Big("OlIconReadAloud", "Immersive Reader", null)),
            Group("OneNote", Big("OlIconOneNote", "Meeting Notes", null)),
            Group("My Templates", Big("OlIconTemplate", "View Templates", null)));
        Tab("Scheduling Assistant", Group("Show", Big("OlIconPeople", "Scheduling Assistant", null), Big("OlIconAddressBook", "Attendee Availability", null)));
        Tab("Insert",
            Group("Include", Big("OlIconPaperclip", "Attach File", null), Big(null, "Attach Item", null), Big(null, "Signature", null)),
            Group("Tables", Big(null, "Table", null)), Group("Illustrations", Big(null, "Pictures", null), Big(null, "Shapes", null)),
            Group("Links", Big(null, "Link", null), Big(null, "Bookmark", null)), Group("Symbols", Big(null, "Equation", null), Big(null, "Symbol", null)));
        Tab("Format Text", Group("Format", Big(null, "Plain Text / HTML", null), Big(null, "Font", null), Big(null, "Styles", null)));
        Tab("Review", Group("Proofing", Big(null, "Spelling & Grammar", null), Big(null, "Thesaurus", null)),
            Group("Speech", Big("OlIconReadAloud", "Read Aloud", null)), Group("Language", Big("OlIconTranslate", "Translate", null)));
        Tab("Help", Group("Help", Big("OlIconHelp", "Help", null), Big(null, "Contact Support", null), Big(null, "Feedback", null)));
        tabs.SelectedIndex = 1;
        return tabs;
    }

    private Control Form()
    {
        var grid = new Grid { Margin = new Thickness(12, 10), ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var save = new Button
        {
            MinWidth = 96, MinHeight = 96, VerticalAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Content = new StackPanel
            {
                Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Children = { Icon("OlIconSaveClose", 34), new TextBlock { Text = "Save & Close", HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap } }
            }
        };
        Wire(save, "Save & Close", () => { Save(); return Task.CompletedTask; });
        save.Margin = new Thickness(0, 0, 16, 0);
        Outlined(save);
        grid.Children.Add(save);

        var form = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,*") };

        var titleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("92,*") };
        titleRow.Children.Add(new TextBlock { Text = "Title", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
        Grid.SetColumn(_subject, 1);
        titleRow.Children.Add(_subject);
        Place(form, titleRow, 0, 0);

        Place(form, new Border { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 6) }, 0, 1);

        var timeZoneContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        timeZoneContent.Children.Add(Icon("OlIconGlobe", 15));
        timeZoneContent.Children.Add(new TextBlock { Text = "Time zones", VerticalAlignment = VerticalAlignment.Center });
        _timeZones.Content = timeZoneContent;
        var startRow = new Grid { ColumnDefinitions = new ColumnDefinitions("92,Auto,Auto,Auto,Auto") };
        startRow.Children.Add(RowLabel("Start time"));
        Grid.SetColumn(_startDate, 1);
        startRow.Children.Add(_startDate);
        Grid.SetColumn(_startTime, 2);
        _startTime.Margin = new Thickness(8, 0, 0, 0);
        startRow.Children.Add(_startTime);
        Grid.SetColumn(_allDay, 3);
        _allDay.Margin = new Thickness(16, 0, 0, 0);
        _allDay.VerticalAlignment = VerticalAlignment.Center;
        startRow.Children.Add(_allDay);
        Grid.SetColumn(_timeZones, 4);
        _timeZones.Margin = new Thickness(16, 0, 0, 0);
        _timeZones.VerticalAlignment = VerticalAlignment.Center;
        startRow.Children.Add(_timeZones);
        Place(form, startRow, 0, 2);

        var recurring = _recurring;
        var endRow = new Grid { ColumnDefinitions = new ColumnDefinitions("92,Auto,Auto,*") };
        endRow.Children.Add(RowLabel("End time"));
        Grid.SetColumn(_endDate, 1);
        endRow.Children.Add(_endDate);
        Grid.SetColumn(_endTime, 2);
        _endTime.Margin = new Thickness(8, 0, 0, 0);
        endRow.Children.Add(_endTime);
        Grid.SetColumn(recurring, 3);
        recurring.Margin = new Thickness(16, 0, 0, 0);
        recurring.HorizontalAlignment = HorizontalAlignment.Left;
        endRow.Children.Add(recurring);
        Place(form, endRow, 0, 3);

        var locationButton = Pill("Location", "Choose a room: Planned Feature. Click to type a location instead.");
        locationButton.Click += (_, _) => _location.Focus();
        var locationRow = new Grid { ColumnDefinitions = new ColumnDefinitions("92,Auto,*"), Margin = new Thickness(0, 8, 0, 0) };
        locationRow.Children.Add(locationButton);
        Grid.SetColumn(_location, 2);
        locationRow.Children.Add(_location);
        Place(form, locationRow, 0, 4);

        var toButton = Pill("To...", "Address book: Planned Feature. Type addresses separated by semicolons.");
        toButton.Click += (_, _) => _attendees.Focus();
        _attendeesRow.ColumnDefinitions = new ColumnDefinitions("92,Auto,*");
        _attendeesRow.Children.Add(toButton);
        Grid.SetColumn(_attendees, 2);
        _attendeesRow.Children.Add(_attendees);
        _attendeesRow.Margin = new Thickness(0, 8, 0, 0);
        Place(form, _attendeesRow, 0, 5);

        var bodyBorder = new Border { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(0, 1, 0, 0), Child = _body, Margin = new Thickness(0, 8, 0, 0) };
        Place(form, bodyBorder, 0, 6);

        Grid.SetColumn(form, 1);
        grid.Children.Add(form);
        return grid;
    }

    private void ToggleAttendees()
    {
        _isMeeting = !_isMeeting;
        _attendeesRow.IsVisible = _isMeeting;
        if (_isMeeting) _attendees.Focus();
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        var subject = string.IsNullOrWhiteSpace(_subject.Text) ? "Untitled" : _subject.Text!.Trim();
        if (_readOnly) { Title = $"{subject} - Meeting (read only)"; return; }
        Title = $"{subject} - {(_isMeeting ? "Meeting" : "Appointment")}";
    }

    // ---------------------------------------------------------------- save

    private void Save()
    {
        if (_readOnly) { Info("Only the organizer can change this meeting."); return; }
        if (Build() is { } item) Close(item);
    }

    internal void SetTimes(DateOnly startDay, TimeOnly start, DateOnly endDay, TimeOnly end)
    { _startDate.Date = startDay; _startTime.Time = start; _endDate.Date = endDay; _endTime.Time = end; }
    internal OutlookDateBox StartDateBox => _startDate;
    internal OutlookTimeBox StartTimeBox => _startTime;
    internal OutlookTimeBox EndTimeBox => _endTime;
    internal OutlookDateBox EndDateBox => _endDate;
    internal void ChooseShowAs(string value) => _showAs.SelectedItem = _showAs.Items.OfType<ComboBoxItem>().First(i => (string?)i.Tag == value);
    internal void SetPrivate(bool on) => _private.IsChecked = on;
    internal void SetImportance(string value) { _high.IsChecked = value == "high"; _low.IsChecked = value == "low"; }
    internal void SetBody(string text) => _body.Text = text;
    internal void RequestDelete() { DeleteRequested = _existing is not null; Close(null); }
    internal bool IsReadOnly => _readOnly;
    internal string StatusText => _status.Text ?? "";

    private string[] Attendees() => _isMeeting
        ? (_attendees.Text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        : [];

    /// <summary>Selects a Reminder choice by its label ("1 hour").</summary>
    internal void ChooseReminder(string label) => _reminder.SelectedIndex = _reminderChoices.FindIndex(c => c.Label == label);
    internal void SetSubject(string subject) => _subject.Text = subject;

    /// <summary>The item as entered, or null after telling the user what is missing.</summary>
    internal NewCalendarEvent? Build()
    {
        var subject = _subject.Text?.Trim() ?? "";
        if (subject.Length is < 1 or > 4096) { Fail("Enter a subject."); return null; }
        var first = _startDate.Date;
        var last = _endDate.Date;
        var allDay = _allDay.IsChecked == true;
        var startTime = TimeOnly.MinValue;
        var endTime = TimeOnly.MinValue;
        if (!allDay) { startTime = _startTime.Time; endTime = _endTime.Time; }
        if (allDay) last = last.AddDays(1); // Graph uses an exclusive midnight end for all-day events.
        var localStart = first.ToDateTime(startTime, DateTimeKind.Unspecified);
        var localEnd = last.ToDateTime(endTime, DateTimeKind.Unspecified);
        if (TimeZoneInfo.Local.IsInvalidTime(localStart) || TimeZoneInfo.Local.IsInvalidTime(localEnd))
        { Fail("That time does not exist in your local time zone. Choose another time."); return null; }
        if (TimeZoneInfo.Local.IsAmbiguousTime(localStart) || TimeZoneInfo.Local.IsAmbiguousTime(localEnd))
        { Fail("That time occurs twice in your local time zone. Choose another time."); return null; }
        var start = new DateTimeOffset(localStart, TimeZoneInfo.Local.GetUtcOffset(localStart));
        var end = new DateTimeOffset(localEnd, TimeZoneInfo.Local.GetUtcOffset(localEnd));
        if (end <= start || end - start > TimeSpan.FromDays(366))
        { Fail("The end must be after the start."); return null; }
        var zone = TimeZoneInfo.Local.Id;
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(zone, out var windowsZone)) zone = windowsZone;
        var attendees = Attendees();
        if (_isMeeting && attendees.Length == 0)
        { Fail("Add at least one attendee for a meeting."); return null; }
        if (attendees.Length > 100 || attendees.Any(address =>
                !MailAddress.TryCreate(address, out var parsed) || parsed.Address != address))
        { Fail("Enter valid attendee email addresses separated by semicolons."); return null; }
        var reminder = _reminderChoices[Math.Clamp(_reminder.SelectedIndex, 0, _reminderChoices.Count - 1)].Minutes;
        var showAs = (_showAs.SelectedItem as ComboBoxItem)?.Tag as string ?? "busy";
        return new NewCalendarEvent(subject, start, end, allDay, _location.Text ?? "", _body.Text ?? "",
            attendees, Guid.NewGuid(), zone, reminder, showAs, _private.IsChecked == true ? "private" : "normal",
            _high.IsChecked == true ? "high" : _low.IsChecked == true ? "low" : "normal");
    }

    private void Fail(string message)
    {
        _status.Foreground = Brushes.Firebrick;
        _status.Text = message;
    }

    // ---- ribbon building blocks (the same shapes ComposeWindow uses) ----

    private Control Group(string caption, params Control[] content)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var c in content) strip.Children.Add(c);
        var dock = new DockPanel { Margin = new Thickness(6, 2) };
        var label = new TextBlock { Text = caption, FontSize = 11, Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
        DockPanel.SetDock(label, Dock.Bottom);
        dock.Children.Add(label);
        dock.Children.Add(strip);
        return new Border { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(0, 0, 4, 0), Child = dock };
    }

    private Button Big(string? icon, string caption, Func<Task>? action)
    {
        var content = new StackPanel { Width = 62, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(icon is not null ? Icon(icon, 28) : new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(4), BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center });
        content.Children.Add(new TextBlock { Text = caption, FontSize = 11, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center });
        return Wire(new Button { Content = content, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(2) }, caption, action);
    }

    private Button Mini(string? icon, string caption, Func<Task>? action)
    {
        object content = icon is not null ? Icon(icon, 16) : new TextBlock { Text = caption, FontSize = 11 };
        return Wire(new Button { Content = content, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(4, 2), MinHeight = 22 }, caption, action);
    }

    private Button Small(string icon, string? caption, Func<Task>? action)
    {
        var b = new Button { Content = Icon(icon, 18), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(4) };
        return Wire(b, caption ?? icon.Replace("OlIcon", ""), action, caption is null);
    }

    private static Button Rotated(Button button, double angle)
    {
        if (button.Content is Control icon) icon.RenderTransform = new RotateTransform(angle);      // the icon (a Viewbox since 2026-10-17)
        return button;
    }

    private Button LinkButton(string caption)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        content.Children.Add(Icon("OlIconRecurrence", 15));
        content.Children.Add(new TextBlock { Text = caption, Foreground = new SolidColorBrush(Color.FromRgb(0x0F, 0x6C, 0xBD)), VerticalAlignment = VerticalAlignment.Center });
        return Wire(new Button { Content = content, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(2) }, caption, null);
    }

    private Button Wire(Button b, string caption, Func<Task>? action, bool forceNotYet = false)
    {
        if (action is null || forceNotYet)
        {
            ToolTip.SetTip(b, NotYet);
            b.Click += (_, _) => { _status.Foreground = Brushes.Gray; _status.Text = $"{caption}: {NotYet.ToLowerInvariant()}."; };
        }
        else
        {
            ToolTip.SetTip(b, caption);
            b.Click += async (_, _) => await action();
        }
        return b;
    }

    private static Control Real(Control c) => c;

    /// <summary>A small ribbon command with its caption beside the icon (Outlook's Calendar / Forward / Private rows).</summary>
    private static Button Labelled(Button b, string caption)
    {
        var icon = b.Content as Control;
        b.Content = null;                                                                              // the icon moves into the new row
        b.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { icon ?? new Border(), new TextBlock { Text = caption, FontSize = 12, VerticalAlignment = VerticalAlignment.Center } } };
        return b;
    }

    /// <summary>Private / High Importance / Low Importance: Outlook's toggles, shaded while on.</summary>
    private static ToggleButton Toggle(ToggleButton t, string icon, string caption)
    {
        t.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Icon(icon, 16), new TextBlock { Text = caption, FontSize = 12, VerticalAlignment = VerticalAlignment.Center } } };
        t.Background = Brushes.Transparent; t.BorderThickness = new Thickness(1); t.BorderBrush = Brushes.Transparent; t.Padding = new Thickness(4, 1);
        t.HorizontalAlignment = HorizontalAlignment.Stretch; t.HorizontalContentAlignment = HorizontalAlignment.Left;
        void Shade() { t.Background = t.IsChecked == true ? new SolidColorBrush(Color.FromRgb(0xC5, 0xDE, 0xF5)) : Brushes.Transparent; t.BorderBrush = t.IsChecked == true ? new SolidColorBrush(Color.FromRgb(0x8E, 0xB8, 0xE5)) : Brushes.Transparent; }
        t.IsCheckedChanged += (_, _) => Shade();
        t.Classes.Add("olApptToggle");
        ToolTip.SetTip(t, caption);
        return t;
    }

    private static StackPanel Stack(params Control[] rows) => new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center }.With(rows);
    private static StackPanel Row(params Control[] items) => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 }.With(items);

    private static Control Icon(string key, double size)
    {
        if (Application.Current is { } app && app.TryFindResource(key, out var template) && template is IControlTemplate ct)
            return OlIcon.Create(ct, size);
        return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(3), BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1) };
    }

    private static TextBlock RowLabel(string text) => new()
    {
        Text = text, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
    };

    private static Button Pill(string text, string tip)
    {
        var b = new Button { Content = text, MinWidth = 62, HorizontalContentAlignment = HorizontalAlignment.Center, CornerRadius = new CornerRadius(4) };
        ToolTip.SetTip(b, tip);
        return Outlined(b);
    }

    /// <summary>Outlook's white button with a thin grey border (Save &amp; Close, Location, To...).</summary>
    private static Button Outlined(Button b)
    {
        b.Background = Themed.Brush("OlCard", Brushes.White);
        b.BorderBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x88, 0x86));
        b.BorderThickness = new Thickness(1);
        return b;
    }

    private static void Place(Grid grid, Control c, int column, int row)
    {
        Grid.SetColumn(c, column); Grid.SetRow(c, row);
        grid.Children.Add(c);
    }

    private void Info(string message)
    {
        _status.Foreground = Brushes.Gray;
        _status.Text = message;
    }
}
