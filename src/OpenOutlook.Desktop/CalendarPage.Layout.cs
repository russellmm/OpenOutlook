using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Desktop;

public sealed partial class CalendarPage
{
    private readonly List<(int View, Button Button)> _viewRibbonButtons = [];

    private Control BuildRibbon(Action? openFile)
    {
        var tabs = new TabControl { Classes = { "olTabs" }, Padding = new Thickness(0) };
        tabs.Items.Add(new TabItem { Header = "File" });
        tabs.Items.Add(new TabItem { Header = "Home", Content = RibbonScroll(BuildHomeRibbon()) });
        tabs.Items.Add(new TabItem { Header = "View", Content = RibbonScroll(BuildViewRibbon()) });
        tabs.Items.Add(new TabItem { Header = "Help", Content = RibbonScroll(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { RibbonGroup("Help", Placeholder("Calendar help", "OlIconHelp")) }
        }) });
        tabs.SelectedIndex = 1;
        tabs.SelectionChanged += (_, _) =>
        {
            if (tabs.SelectedIndex != 0 || openFile is null) return;
            tabs.SelectedIndex = 1;
            openFile();
        };
        return tabs;
    }

    private StackPanel BuildHomeRibbon()
    {
        ConfigureRibbonButton(_appointment, "New\nAppointment", "OlIconCalendar");
        ConfigureRibbonButton(_meeting, "New\nMeeting", "OlIconPeople");
        _appointment.Click += async (_, _) => await CreateAsync(false);
        _meeting.Click += async (_, _) => await CreateAsync(true);

        var newGroup = RibbonGroup("New", _appointment, _meeting,
            Placeholder("New\nItems", "OlIconNewItems"));
        var today = RibbonButton("Today", "OlIconCalendar");
        today.Click += async (_, _) => await GoTodayAsync();
        var nextSeven = RibbonButton("Next 7\nDays", "OlIconCalendar");
        nextSeven.Click += async (_, _) =>
        {
            _cursor = DateOnly.FromDateTime(DateTime.Today);
            _views.SelectedIndex = 1;
            await RefreshEventsAsync();
        };
        var goTo = RibbonGroup("Go To", today, nextSeven);
        var arrange = RibbonGroup("Arrange", ViewButton("Day", 3), ViewButton("Work\nWeek", 2),
            ViewButton("Week", 1), ViewButton("Month", 0), Placeholder("Schedule\nView", "OlIconCalendar"));
        var calendars = RibbonGroup("Manage Calendars", Placeholder("Open\nCalendar", "OlIconCalendar"),
            Placeholder("Calendar\nGroups", "OlIconPeople"));
        var find = RibbonGroup("Find", Placeholder("Search\nPeople", "OlIconSearch"),
            Placeholder("Address\nBook", "OlIconAddressBook"));
        var refresh = RibbonButton("Refresh", "OlIconRefresh");
        refresh.Click += async (_, _) => await RefreshEventsAsync();
        var update = RibbonGroup("Update", refresh);
        return new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 3),
            Children = { newGroup, goTo, arrange, calendars, find, update } };
    }

    private StackPanel BuildViewRibbon()
    {
        var views = RibbonGroup("Current View", ViewButton("Day", 3), ViewButton("Work\nWeek", 2),
            ViewButton("Week", 1), ViewButton("Month", 0));
        var calendars = RibbonGroup("Calendars", Placeholder("Side by\nSide", "OlIconCalendar"),
            Placeholder("Overlay", "OlIconCalendar"));
        var layout = RibbonGroup("Layout", Placeholder("Time\nScale", "OlIconCalendar"),
            Placeholder("Color", "OlIconCategorize"), Placeholder("Layout", "OlIconReadingPane"));
        return new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 3),
            Children = { views, calendars, layout } };
    }

    private static Control RibbonScroll(Control content) => new ScrollViewer
    {
        Content = content,
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
    };

    private static Border RibbonGroup(string caption, params Control[] commands)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var command in commands) content.Children.Add(command);
        var group = new DockPanel { LastChildFill = true };
        var label = new TextBlock { Text = caption, Classes = { "olGroupLabel" } };
        DockPanel.SetDock(label, Dock.Bottom);
        group.Children.Add(label);
        group.Children.Add(content);
        return new Border { Classes = { "olGroup" }, Child = group };
    }

    private static Control Icon(string key, double size)
    {
        if (Application.Current is { } app && app.TryFindResource(key, out var template) &&
            template is IControlTemplate controlTemplate)
            return new ContentControl { Template = controlTemplate, Width = size, Height = size,
                HorizontalAlignment = HorizontalAlignment.Center };
        return new TextBlock { Text = "▦", FontSize = size, HorizontalAlignment = HorizontalAlignment.Center };
    }

    private static void ConfigureRibbonButton(Button button, string caption, string icon)
    {
        button.Classes.Add("olLarge");
        button.Content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center,
            Children = { Icon(icon, 28), new TextBlock { Text = caption, Classes = { "olCaption" } } } };
    }

    private static Button RibbonButton(string caption, string icon)
    {
        var button = new Button();
        ConfigureRibbonButton(button, caption, icon);
        return button;
    }

    private static Button Placeholder(string caption, string icon)
    {
        var button = RibbonButton(caption, icon);
        button.IsEnabled = false;
        ToolTip.SetTip(button, "Planned feature");
        return button;
    }

    private Button ViewButton(string caption, int view)
    {
        var button = RibbonButton(caption, "OlIconCalendar");
        button.Click += async (_, _) =>
        {
            _views.SelectedIndex = view;
            await RefreshEventsAsync();
        };
        _viewRibbonButtons.Add((view, button));
        return button;
    }

    private async Task GoTodayAsync()
    {
        _cursor = DateOnly.FromDateTime(DateTime.Today);
        await RefreshEventsAsync();
    }

    private void UpdateRibbonViewSelection()
    {
        foreach (var (view, button) in _viewRibbonButtons)
            button.Classes.Set("olCalViewSelected", view == _views.SelectedIndex);
    }

    private Control BuildSidebar()
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(8, 10) };
        panel.Children.Add(_miniHost);
        panel.Children.Add(new Border { Height = 1, Background = Brushes.Gray, Margin = new Thickness(0, 4) });
        panel.Children.Add(new TextBlock { Text = "My Calendars", FontSize = 14,
            FontWeight = FontWeight.SemiBold, Margin = new Thickness(3, 1, 0, 2) });
        panel.Children.Add(new TextBlock { Text = "Connected Microsoft account", FontSize = 11,
            Margin = new Thickness(3, 0, 0, 0) });
        panel.Children.Add(_accounts);
        panel.Children.Add(_calendars); // selection state; the visible choices are listed below.
        panel.Children.Add(_calendarChoices);
        panel.Children.Add(_connect);
        return new Border { Classes = { "olCalendarSidebar" },
            Child = new ScrollViewer { Content = panel } };
    }

    private void RenderCalendarChoices()
    {
        _calendarChoices.Children.Clear();
        var selected = SelectedCalendar;
        foreach (var option in _calendars.Items.OfType<ComboBoxItem>())
        {
            if (option.Tag is not GraphCalendar calendar) continue;
            var row = new RadioButton
            {
                Content = calendar.Name + (calendar.CanEdit ? "" : " (read only)"),
                IsChecked = calendar.Id == selected?.Id,
                GroupName = "OpenOutlookCalendarSource",
                Margin = new Thickness(5, 0, 3, 0)
            };
            row.Click += (_, _) => _calendars.SelectedItem = option;
            _calendarChoices.Children.Add(row);
        }
    }

    private void RenderMiniMonths()
    {
        _miniHost.Children.Clear();
        var first = new DateOnly(_cursor.Year, _cursor.Month, 1);
        _miniHost.Children.Add(MiniMonth(first, true));
        _miniHost.Children.Add(MiniMonth(first.AddMonths(1), false));
    }

    private Control MiniMonth(DateOnly month, bool navigation)
    {
        var panel = new StackPanel { Spacing = 2 };
        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("25,*,25"), Margin = new Thickness(2, 0, 2, 4) };
        var caption = new TextBlock { Text = month.ToString("MMMM yyyy"),
            FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(caption, 1);
        title.Children.Add(caption);
        if (navigation)
        {
            var previous = MiniNav("‹", -1);
            var next = MiniNav("›", 1);
            Grid.SetColumn(next, 2);
            title.Children.Add(previous);
            title.Children.Add(next);
        }
        panel.Children.Add(title);
        var grid = new Grid();
        for (var col = 0; col < 7; col++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var weekday = new TextBlock { Text = ((DayOfWeek)col).ToString()[..2],
                FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center };
            Grid.SetColumn(weekday, col);
            grid.Children.Add(weekday);
        }
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var start = month.AddDays(-(int)month.DayOfWeek);
        for (var row = 0; row < 6; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var col = 0; col < 7; col++)
            {
                var date = start.AddDays(row * 7 + col);
                var button = new Button { Content = date.Day.ToString(),
                    Classes = { "olMiniDay" }, Opacity = date.Month == month.Month ? 1 : 0.55,
                    FontWeight = _events.Any(item => OccursOn(item, date)) ? FontWeight.Bold : FontWeight.Normal };
                button.Classes.Set("olMiniSelected", date == _cursor);
                button.Click += async (_, _) => { _cursor = date; await RefreshEventsAsync(); };
                Grid.SetColumn(button, col);
                Grid.SetRow(button, row + 1);
                grid.Children.Add(button);
            }
        }
        panel.Children.Add(grid);
        return panel;
    }

    private Button MiniNav(string caption, int months)
    {
        var button = new Button { Content = caption, Classes = { "olMiniNav" } };
        button.Click += async (_, _) => { _cursor = _cursor.AddMonths(months); await RefreshEventsAsync(); };
        return button;
    }
}
