using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OpenOutlook.Auth;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Desktop;

/// <summary>The personal Microsoft calendar section in the main navigation shell.</summary>
public sealed partial class CalendarPage : UserControl
{
    private readonly Window _owner;
    private readonly HttpClient _http;
    private readonly Func<ConnectedAccount, Task<string>> _token;
    private readonly Func<ConnectedAccount?, Task> _accountSetup;
    private readonly ComboBox _accounts = new() { MinWidth = 205 };
    private readonly ComboBox _calendars = new() { IsVisible = false };
    private readonly ComboBox _views = new() { MinWidth = 105 };
    private readonly StackPanel _miniHost = new() { Spacing = 14 };
    private readonly StackPanel _calendarChoices = new() { Spacing = 3 };
    private readonly TextBlock _heading = new() { FontSize = 20, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _connect = new() { Content = "Connect or reconnect account", IsVisible = false };
    private readonly Button _appointment = new() { Content = "New appointment" };
    private readonly Button _meeting = new() { Content = "New meeting" };
    private readonly Button _retry = new() { Content = "Retry save", IsVisible = false };
    private readonly Button _discard = new() { Content = "Dismiss retry", IsVisible = false };
    private readonly Grid _days = new();
    private readonly ScrollViewer _scroll = new();
    private IReadOnlyList<GraphCalendarEvent> _events = [];
    private DateOnly _cursor = DateOnly.FromDateTime(DateTime.Today);
    private CancellationTokenSource? _loadCancellation;
    private long _loadVersion;
    private bool _active;
    private bool _settingCalendars;
    private (ConnectedAccount Account, GraphCalendar Calendar, NewCalendarEvent Draft, bool Meeting)? _pendingCreate;
    public Control Ribbon { get; }

    public CalendarPage(Window owner, HttpClient http, Func<ConnectedAccount, Task<string>> token,
        Func<ConnectedAccount?, Task> accountSetup, Action? openFile = null)
    {
        _owner = owner;
        _http = http;
        _token = token;
        _accountSetup = accountSetup;
        Ribbon = BuildRibbon(openFile);

        var page = new Grid { ColumnDefinitions = new ColumnDefinitions("255,*") };
        page.Children.Add(BuildSidebar());
        var main = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 0) };
        Grid.SetColumn(main, 1);
        page.Children.Add(main);

        var header = new StackPanel { Spacing = 5, Margin = new Thickness(12, 10, 12, 7) };
        var navigation = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto") };
        var today = new Button { Content = "Today", MinWidth = 58, Margin = new Thickness(0, 0, 6, 0) };
        var previous = new Button { Content = "‹", MinWidth = 30, Margin = new Thickness(0, 0, 4, 0) };
        var next = new Button { Content = "›", MinWidth = 30, Margin = new Thickness(0, 0, 10, 0) };
        Grid.SetColumn(previous, 1);
        Grid.SetColumn(next, 2);
        Grid.SetColumn(_heading, 3);
        Grid.SetColumn(_views, 4);
        _heading.VerticalAlignment = VerticalAlignment.Center;
        navigation.Children.Add(today);
        navigation.Children.Add(previous);
        navigation.Children.Add(next);
        navigation.Children.Add(_heading);
        _views.ItemsSource = new[] { "Month", "Week", "Work week", "Day" };
        _views.SelectedIndex = 0;
        navigation.Children.Add(_views);
        header.Children.Add(navigation);
        header.Children.Add(_message);
        var retryActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        retryActions.Children.Add(_retry);
        retryActions.Children.Add(_discard);
        header.Children.Add(retryActions);
        DockPanel.SetDock(header, Dock.Top);
        main.Children.Add(header);
        _scroll.Content = _days;
        main.Children.Add(_scroll);
        Content = page;

        previous.Click += async (_, _) => { Move(-1); await RefreshEventsAsync(); };
        next.Click += async (_, _) => { Move(1); await RefreshEventsAsync(); };
        today.Click += async (_, _) => { _cursor = DateOnly.FromDateTime(DateTime.Today); await RefreshEventsAsync(); };
        _views.SelectionChanged += async (_, _) => { if (_active) await RefreshEventsAsync(); };
        _accounts.SelectionChanged += async (_, _) => { if (_active) await LoadCalendarsAsync(); };
        _calendars.SelectionChanged += async (_, _) =>
        {
            if (_active && !_settingCalendars)
            {
                UpdateCreateAvailability();
                RenderCalendarChoices();
                await RefreshEventsAsync();
            }
        };
        _connect.Click += async (_, _) =>
        {
            await _accountSetup(SelectedAccount);
            if (_active) await LoadCalendarsAsync();
        };
        _retry.Click += async (_, _) => await SavePendingAsync();
        _discard.Click += (_, _) =>
        {
            _pendingCreate = null;
            _retry.IsVisible = _discard.IsVisible = false;
            _message.Text = "Pending save dismissed. Refresh the calendar to check whether it was created.";
            UpdateCreateAvailability();
        };
        Render();
    }

    public void SetAccounts(IReadOnlyList<ConnectedAccount> accounts)
    {
        _loadCancellation?.Cancel();
        _loadVersion++;
        var selectedId = SelectedAccount?.AccountId;
        _accounts.Items.Clear();
        foreach (var account in accounts.Where(item => item.Provider == OAuthProvider.MicrosoftConsumers))
            _accounts.Items.Add(new ComboBoxItem { Content = account.DisplayAddress, Tag = account });
        _accounts.SelectedItem = _accounts.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => (item.Tag as ConnectedAccount)?.AccountId == selectedId)
            ?? _accounts.Items.OfType<ComboBoxItem>().FirstOrDefault();
        if (_accounts.Items.Count == 0)
        {
            _message.Text = "Connect an Outlook.com or Hotmail account to use Calendar.";
            _connect.IsVisible = true;
            _calendars.Items.Clear();
            RenderCalendarChoices();
            _events = [];
            Render();
        }
        UpdateCreateAvailability();
    }

    public async Task ActivateAsync()
    {
        _active = true;
        await LoadCalendarsAsync();
    }

    public void Deactivate()
    {
        _active = false;
        _loadCancellation?.Cancel();
    }

    public string? SelectedAccountId => SelectedAccount?.AccountId;

    public void SelectAccount(string accountId)
    {
        var option = _accounts.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => (item.Tag as ConnectedAccount)?.AccountId == accountId);
        if (option is not null && !ReferenceEquals(_accounts.SelectedItem, option))
            _accounts.SelectedItem = option;
    }

    private ConnectedAccount? SelectedAccount => (_accounts.SelectedItem as ComboBoxItem)?.Tag as ConnectedAccount;
    private GraphCalendar? SelectedCalendar => (_calendars.SelectedItem as ComboBoxItem)?.Tag as GraphCalendar;

    private async Task LoadCalendarsAsync()
    {
        if (!_active) return;
        var (version, ct) = StartLoad();
        var account = SelectedAccount;
        _events = [];
        _settingCalendars = true;
        _calendars.Items.Clear();
        _settingCalendars = false;
        RenderCalendarChoices();
        UpdateCreateAvailability();
        Render();
        if (account is null)
        {
            _message.Text = "Connect an Outlook.com or Hotmail account to use Calendar.";
            _connect.Content = "Add Microsoft account";
            _connect.IsVisible = true;
            return;
        }
        if (!account.CanWriteMicrosoftCalendar)
        {
            _message.Text = $"Calendar access is not saved for {account.DisplayAddress}. Repair this account's sign-in to create events.";
            _connect.Content = "Repair selected account sign-in";
            _connect.IsVisible = true;
            return;
        }
        _connect.IsVisible = false;
        _message.Text = "Loading calendars…";
        try
        {
            var token = await _token(account);
            var list = await new GraphCalendarClient(_http, account.AccountId).ListCalendarsAsync(token, ct);
            if (ct.IsCancellationRequested || version != _loadVersion) return;
            _settingCalendars = true;
            foreach (var calendar in list)
                _calendars.Items.Add(new ComboBoxItem { Content = calendar.Name, Tag = calendar });
            _calendars.SelectedItem = _calendars.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => (item.Tag as GraphCalendar)?.IsDefault == true)
                ?? _calendars.Items.OfType<ComboBoxItem>().FirstOrDefault();
            _settingCalendars = false;
            RenderCalendarChoices();
            UpdateCreateAvailability();
            if (list.Count == 0) _message.Text = "No calendars were found for this account.";
            else await RefreshEventsAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (version == _loadVersion) _message.Text = "Could not load calendars: " + ex.Message; }
        finally { _settingCalendars = false; }
    }

    private async Task RefreshEventsAsync()
    {
        Render();
        if (!_active || SelectedAccount is not { } account || SelectedCalendar is not { } calendar) return;
        var (version, ct) = StartLoad();
        _message.Text = "Loading events…";
        try
        {
            var token = await _token(account);
            var (start, end) = Range();
            var events = await new GraphCalendarClient(_http, account.AccountId)
                .GetEventsAsync(token, calendar.Id, start, end, ct);
            if (ct.IsCancellationRequested || version != _loadVersion) return;
            _events = events;
            _message.Text = events.Count == 0 ? "No events in this view." : $"{events.Count} event(s)";
            Render();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (version == _loadVersion) _message.Text = "Could not load events: " + ex.Message; }
    }

    private async Task CreateAsync(bool meeting)
    {
        if (_pendingCreate is not null)
        {
            _message.Text = "Retry or dismiss the previous save before creating another event.";
            return;
        }
        if (SelectedAccount is not { } account || SelectedCalendar is not { } calendar)
        {
            _message.Text = "Select a connected Microsoft account and calendar first.";
            return;
        }
        if (!calendar.CanEdit)
        {
            _message.Text = "This calendar is read-only. Choose an editable calendar.";
            return;
        }
        var draft = await new CalendarEditorWindow(_cursor, meeting).ShowDialog<NewCalendarEvent?>(_owner);
        if (draft is null) return;
        _pendingCreate = (account, calendar, draft, meeting);
        await SavePendingAsync();
    }

    private async Task SavePendingAsync()
    {
        if (_pendingCreate is not { } pending) return;
        _appointment.IsEnabled = _meeting.IsEnabled = false;
        _retry.IsEnabled = false;
        _message.Text = pending.Meeting ? "Saving meeting and sending invitations…" : "Saving appointment…";
        try
        {
            var token = await _token(pending.Account);
            var saved = await new GraphCalendarClient(_http, pending.Account.AccountId)
                .CreateEventAsync(token, pending.Calendar.Id, pending.Draft);
            _events = _events.Where(item => item.Id != saved.Id).Append(saved).OrderBy(item => item.Start).ToArray();
            _message.Text = pending.Meeting ? "Meeting saved; invitations were sent by Outlook."
                : "Appointment saved.";
            _pendingCreate = null;
            _retry.IsVisible = _discard.IsVisible = false;
            Render();
            // The returned event is shown immediately. A later navigation fetches Graph's current view.
        }
        catch (Exception ex)
        {
            _message.Text = "Save was not confirmed: " + ex.Message + " Retry uses the same transaction ID.";
            _retry.IsVisible = _discard.IsVisible = true;
        }
        finally
        {
            _retry.IsEnabled = true;
            UpdateCreateAvailability();
        }
    }

    private void UpdateCreateAvailability()
    {
        var canCreate = _pendingCreate is null && SelectedAccount?.CanWriteMicrosoftCalendar == true &&
            SelectedCalendar?.CanEdit == true;
        _appointment.IsEnabled = _meeting.IsEnabled = canCreate;
        var reason = _pendingCreate is not null ? "Finish the pending save first."
            : SelectedAccount is null ? "Connect an Outlook.com or Hotmail account first."
            : !SelectedAccount.CanWriteMicrosoftCalendar
                ? "Calendar access was not saved in OpenOutlook. Reconnect, confirm the account identity, and wait for the connected message."
            : SelectedCalendar is null ? "Wait for calendars to load, or use the message below to resolve a loading error."
            : !SelectedCalendar.CanEdit ? "Select an editable calendar."
            : "Create an event in the selected calendar.";
        ToolTip.SetTip(_appointment, reason);
        ToolTip.SetTip(_meeting, reason);
    }

    private (long Version, CancellationToken Token) StartLoad()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        return (++_loadVersion, _loadCancellation.Token);
    }

    private void Move(int direction)
    {
        _cursor = _views.SelectedIndex switch
        {
            0 => _cursor.AddMonths(direction),
            1 or 2 => _cursor.AddDays(direction * 7),
            _ => _cursor.AddDays(direction)
        };
    }

    private (DateTimeOffset Start, DateTimeOffset End) Range()
    {
        DateOnly first;
        int count;
        switch (_views.SelectedIndex)
        {
            case 0:
                var month = new DateOnly(_cursor.Year, _cursor.Month, 1);
                first = month.AddDays(-(int)month.DayOfWeek);
                count = 42;
                break;
            case 1:
                first = _cursor.AddDays(-(int)_cursor.DayOfWeek);
                count = 7;
                break;
            case 2:
                first = _cursor.AddDays(-((int)_cursor.DayOfWeek + 6) % 7);
                count = 5;
                break;
            default:
                first = _cursor;
                count = 1;
                break;
        }
        static DateTimeOffset LocalMidnight(DateOnly day)
        {
            var date = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            return new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date));
        }
        return (LocalMidnight(first), LocalMidnight(first.AddDays(count)));
    }

    private void Render()
    {
        RenderMiniMonths();
        UpdateRibbonViewSelection();
        _days.Children.Clear();
        _days.RowDefinitions.Clear();
        _days.ColumnDefinitions.Clear();
        if (_views.SelectedIndex == 0) RenderMonth();
        else RenderAgenda();
    }

    private void RenderMonth()
    {
        _heading.Text = _cursor.ToString("MMMM yyyy");
        var first = new DateOnly(_cursor.Year, _cursor.Month, 1);
        first = first.AddDays(-(int)first.DayOfWeek);
        for (var column = 0; column < 7; column++)
        {
            _days.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var label = new TextBlock { Text = ((DayOfWeek)column).ToString(),
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(3, 4) };
            Grid.SetColumn(label, column);
            _days.Children.Add(label);
        }
        _days.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var row = 0; row < 6; row++)
        {
            _days.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
            for (var column = 0; column < 7; column++)
            {
                var date = first.AddDays(row * 7 + column);
                var panel = new StackPanel { Spacing = 2, Margin = new Thickness(5) };
                var day = new Button { Content = date.Day.ToString(), Classes = { "olCalendarDate" },
                    FontWeight = date == DateOnly.FromDateTime(DateTime.Today) ? FontWeight.Bold : FontWeight.Normal };
                day.Click += async (_, _) => { _cursor = date; _views.SelectedIndex = 3; await RefreshEventsAsync(); };
                panel.Children.Add(day);
                foreach (var item in _events.Where(item => OccursOn(item, date)).Take(3))
                    panel.Children.Add(EventLabel(item));
                var extra = _events.Count(item => OccursOn(item, date)) - 3;
                if (extra > 0) panel.Children.Add(new TextBlock { Text = $"+{extra} more", FontSize = 11 });
                var border = new Border { Child = panel, Classes = { "olCalendarCell" }, MinHeight = 80,
                    Opacity = date.Month == _cursor.Month ? 1 : 0.65 };
                Grid.SetColumn(border, column);
                Grid.SetRow(border, row + 1);
                _days.Children.Add(border);
            }
        }
    }

    private void RenderAgenda()
    {
        var (start, end) = Range();
        var first = DateOnly.FromDateTime(start.LocalDateTime);
        var count = (int)(end.LocalDateTime.Date - start.LocalDateTime.Date).TotalDays;
        _heading.Text = count == 1 ? first.ToString("dddd, MMMM d, yyyy")
            : $"{first:MMM d} – {first.AddDays(count - 1):MMM d, yyyy}";
        _days.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(55)));
        _days.RowDefinitions.Add(new RowDefinition(new GridLength(43)));
        _days.RowDefinitions.Add(new RowDefinition(new GridLength(36)));
        for (var hour = 8; hour <= 18; hour++)
            _days.RowDefinitions.Add(new RowDefinition(new GridLength(66)));
        for (var hour = 8; hour <= 18; hour++)
        {
            var label = new TextBlock { Text = new DateTime(2000, 1, 1, hour, 0, 0).ToString("h tt"),
                FontSize = 11, Margin = new Thickness(5, 2, 2, 0), HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetRow(label, hour - 6);
            _days.Children.Add(label);
        }
        for (var index = 0; index < count; index++)
        {
            var date = first.AddDays(index);
            var column = index + 1;
            _days.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var header = new StackPanel { Margin = new Thickness(8, 4), Spacing = 1 };
            header.Children.Add(new TextBlock { Text = date.ToString("dddd"), FontSize = 13,
                FontWeight = FontWeight.SemiBold });
            header.Children.Add(new TextBlock { Text = date.ToString("MMM d"), FontSize = 11 });
            var headerBorder = new Border { Classes = { "olCalendarCell" }, Child = header };
            Grid.SetColumn(headerBorder, column);
            _days.Children.Add(headerBorder);
            var allDay = new StackPanel { Margin = new Thickness(3), Spacing = 1 };
            foreach (var item in _events.Where(item => item.IsAllDay && OccursOn(item, date)).Take(2))
                allDay.Children.Add(EventLabel(item));
            var allDayBorder = new Border { Classes = { "olCalendarCell" }, Child = allDay };
            Grid.SetColumn(allDayBorder, column);
            Grid.SetRow(allDayBorder, 1);
            _days.Children.Add(allDayBorder);
            for (var hour = 8; hour <= 18; hour++)
            {
                var cell = new Border { Classes = { "olCalendarCell" } };
                Grid.SetColumn(cell, column);
                Grid.SetRow(cell, hour - 6);
                _days.Children.Add(cell);
            }
            var canvas = TimedDayCanvas(date);
            Grid.SetColumn(canvas, column);
            Grid.SetRow(canvas, 2);
            Grid.SetRowSpan(canvas, 11);
            _days.Children.Add(canvas);
        }
    }

    private Canvas TimedDayCanvas(DateOnly date)
    {
        const double pixelsPerHour = 66;
        var canvas = new Canvas { Height = 11 * pixelsPerHour, ClipToBounds = true };
        var dayStart = date.ToDateTime(new TimeOnly(8, 0));
        var dayEnd = date.ToDateTime(new TimeOnly(19, 0));
        var items = _events.Where(item => !item.IsAllDay && OccursOn(item, date))
            .Select(item => (Event: item, Start: item.Start.ToLocalTime().LocalDateTime,
                End: item.End.ToLocalTime().LocalDateTime))
            .Where(item => item.End > dayStart && item.Start < dayEnd)
            .OrderBy(item => item.Start).ToArray();
        var laneEnds = new List<DateTime>();
        var cards = new List<(Border Card, int Lane)>();
        foreach (var item in items)
        {
            var lane = laneEnds.FindIndex(lastEnd => lastEnd <= item.Start);
            if (lane < 0) { lane = laneEnds.Count; laneEnds.Add(item.End); }
            else laneEnds[lane] = item.End;
            var visibleStart = item.Start < dayStart ? dayStart : item.Start;
            var visibleEnd = item.End > dayEnd ? dayEnd : item.End;
            var card = (Border)EventLabel(item.Event);
            card.Child = new TextBlock
            {
                Text = item.Event.Subject + (string.IsNullOrWhiteSpace(item.Event.Location) ? "" :
                    "\n" + item.Event.Location),
                FontSize = 11, TextWrapping = TextWrapping.Wrap
            };
            card.Height = Math.Max(24, (visibleEnd - visibleStart).TotalHours * pixelsPerHour - 2);
            card.ClipToBounds = true;
            Canvas.SetTop(card, (visibleStart - dayStart).TotalHours * pixelsPerHour + 1);
            canvas.Children.Add(card);
            cards.Add((card, lane));
        }
        void SizeCards()
        {
            var lanes = Math.Max(1, laneEnds.Count);
            var width = Math.Max(35, (canvas.Bounds.Width - 4) / lanes);
            foreach (var (card, lane) in cards)
            {
                card.Width = width - 2;
                Canvas.SetLeft(card, lane * width + 2);
            }
        }
        canvas.SizeChanged += (_, _) => SizeCards();
        return canvas;
    }

    private static bool OccursOn(GraphCalendarEvent item, DateOnly day)
    {
        if (item.IsAllDay)
        {
            var start = DateOnly.FromDateTime(item.Start.ToLocalTime().DateTime);
            var end = DateOnly.FromDateTime(item.End.ToLocalTime().DateTime);
            return start <= day && day < end;
        }
        var localStart = item.Start.ToLocalTime();
        var localEnd = item.End.ToLocalTime();
        var dayStart = day.ToDateTime(TimeOnly.MinValue);
        var dayEnd = day.AddDays(1).ToDateTime(TimeOnly.MinValue);
        return localStart.LocalDateTime < dayEnd && localEnd.LocalDateTime > dayStart;
    }

    private static Control EventLabel(GraphCalendarEvent item)
    {
        var time = item.IsAllDay ? "All day" : item.Start.ToLocalTime().ToString("t");
        var label = new TextBlock { Text = $"{time}  {item.Subject}",
            TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11 };
        var card = new Border { Classes = { "olCalendarEvent" }, Child = label };
        ToolTip.SetTip(card, string.IsNullOrWhiteSpace(item.Location) ? item.Subject :
            item.Subject + " — " + item.Location);
        return card;
    }
}
