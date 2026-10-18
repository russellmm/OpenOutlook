using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace OpenOutlook.Desktop;

/// <summary>
/// Outlook's date box (owner's screenshot 2026-10-17): "Sat 10/17/2026" that can be typed in, a calendar button opening a month with a Today button.
/// </summary>
public sealed class OutlookDateBox : UserControl
{
    /// <summary>One thin frame around the text and its button (Outlook's look); the frame turns blue while the box has the keyboard.</summary>
    internal static Border Frame(Control content)
    {
        var frame = new Border
        {
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Background = Themed.Brush("OlCard", Brushes.White),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x88, 0x86)), Child = content, MinHeight = 30,
        };
        content.AddHandler(InputElement.GotFocusEvent, (_, _) => frame.BorderBrush = new SolidColorBrush(Color.FromRgb(0x0F, 0x6C, 0xBD)), RoutingStrategies.Bubble);
        content.AddHandler(InputElement.LostFocusEvent, (_, _) => frame.BorderBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x88, 0x86)), RoutingStrategies.Bubble);
        return frame;
    }

    private readonly TextBox _text = new() { MinWidth = 150, BorderThickness = new Thickness(0), Background = Brushes.Transparent, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Button _button = new() { Padding = new Thickness(6, 2), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
    private readonly Popup _popup;
    private readonly Avalonia.Controls.Calendar _calendar = new() { SelectionMode = CalendarSelectionMode.SingleDate, DisplayMode = CalendarMode.Month, BorderThickness = new Thickness(0) };
    private DateOnly _date = DateOnly.FromDateTime(DateTime.Today);
    private bool _setting;

    public event Action<DateOnly>? DateChanged;

    public OutlookDateBox()
    {
        _button.Content = OlIcon.Create(OlIcon.Template("OlIconCalendar"), 18);
        ToolTip.SetTip(_button, "Choose a date");
        var today = new Button { Content = "Today", HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(24, 4), Margin = new Thickness(0, 4, 0, 8) };
        today.Click += (_, _) => { Date = DateOnly.FromDateTime(DateTime.Today); _popup!.IsOpen = false; };
        _calendar.SelectedDatesChanged += (_, _) =>
        {
            if (_setting || _calendar.SelectedDate is not { } d) return;
            Date = DateOnly.FromDateTime(d);
            _popup!.IsOpen = false;
        };
        _popup = new Popup
        {
            PlacementTarget = _text, Placement = PlacementMode.BottomEdgeAlignedLeft, IsLightDismissEnabled = true,
            Child = new Border
            {
                Background = Themed.Brush("OlCard", Brushes.White), BorderBrush = Themed.Brush("OlHairline", Brushes.LightGray), BorderThickness = new Thickness(1),
                Child = new StackPanel { Children = { _calendar, new Border { Height = 1, Background = Themed.Brush("OlHairline", Brushes.LightGray) }, today } },
            },
        };
        _button.Click += (_, _) =>
        {
            _setting = true;
            _calendar.SelectedDate = _calendar.DisplayDate = _date.ToDateTime(TimeOnly.MinValue);
            _setting = false;
            _popup.IsOpen = !_popup.IsOpen;
        };
        _text.LostFocus += (_, _) => Commit();
        _text.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { _text, _popup } };
        Grid.SetColumn(_button, 1);
        grid.Children.Add(_button);
        Content = Frame(grid);
        Show();
    }

    public DateOnly Date
    {
        get => _date;
        set { var changed = value != _date; _date = value; Show(); if (changed) DateChanged?.Invoke(value); }
    }

    /// <summary>Outlook's format: the day's short name and the short date ("Sat 10/17/2026").</summary>
    public static string Format(DateOnly d) => d.ToString("ddd", CultureInfo.CurrentCulture) + " " + d.ToString("d", CultureInfo.CurrentCulture);

    /// <summary>Reads what was typed: the short date with or without the day name ("10/20", "Tue 10/20/2026", "2026-10-20").</summary>
    public static DateOnly? Parse(string? text, DateOnly current)
    {
        var t = (text ?? "").Trim();
        var space = t.IndexOf(' ');
        if (space > 0 && !char.IsDigit(t[0])) t = t[(space + 1)..].Trim();                       // "Sat 10/17/2026" -> "10/17/2026"
        if (DateOnly.TryParse(t, CultureInfo.CurrentCulture, DateTimeStyles.None, out var d)) return d;
        if (DateOnly.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return d;
        if (DateTime.TryParse(t + "/" + current.Year, CultureInfo.CurrentCulture, DateTimeStyles.None, out var md)) return DateOnly.FromDateTime(md);   // "10/20"
        return null;
    }

    internal void Type(string text) { _text.Text = text; Commit(); }
    internal string Shown => _text.Text ?? "";
    internal bool IsCalendarOpen => _popup.IsOpen;
    internal void OpenCalendar() => _button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    internal void PickInCalendar(DateOnly d) { _calendar.SelectedDate = d.ToDateTime(TimeOnly.MinValue); }

    public new bool IsEnabled { get => _text.IsEnabled; set { _text.IsEnabled = _button.IsEnabled = value; } }

    private void Commit()
    {
        if (Parse(_text.Text, _date) is { } d) Date = d;
        else Show();                                                                                 // not a date: back to the last one
    }

    private void Show() => _text.Text = Format(_date);
}

/// <summary>
/// Outlook's time box: a time that can be typed ("8:15 AM", "20:30", "8p") and a list of the day's half hours; the end box lists durations from the
/// start ("8:30 AM (30 minutes)") when both are on the same day.
/// </summary>
public sealed class OutlookTimeBox : UserControl
{
    private readonly TextBox _text = new() { MinWidth = 150, BorderThickness = new Thickness(0), Background = Brushes.Transparent, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Button _button = new() { Padding = new Thickness(8, 2), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
    private readonly ListBox _list = new() { MaxHeight = 220, MinWidth = 180 };
    private readonly Popup _popup;
    private TimeOnly _time = new(8, 0);

    public event Action<TimeOnly>? TimeChanged;
    /// <summary>For the end box: the start, to show durations in the list (null: plain times).</summary>
    public Func<TimeOnly?>? DurationFrom { get; set; }

    public OutlookTimeBox()
    {
        _button.Content = new TextBlock { Text = "\u25be", FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Foreground = Themed.Brush("OlText", Brushes.Black) };
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is ListBoxItem { Tag: TimeOnly t } && _popup!.IsOpen) { Time = t; _popup.IsOpen = false; }
        };
        _popup = new Popup
        {
            PlacementTarget = _text, Placement = PlacementMode.BottomEdgeAlignedLeft, IsLightDismissEnabled = true,
            Child = new Border { Background = Themed.Brush("OlCard", Brushes.White), BorderBrush = Themed.Brush("OlHairline", Brushes.LightGray), BorderThickness = new Thickness(1), Child = _list },
        };
        _button.Click += (_, _) => { if (_popup.IsOpen) _popup.IsOpen = false; else OpenList(); };
        _text.LostFocus += (_, _) => Commit();
        _text.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { _text, _popup } };
        Grid.SetColumn(_button, 1);
        grid.Children.Add(_button);
        Content = OutlookDateBox.Frame(grid);
        Show();
    }

    public TimeOnly Time
    {
        get => _time;
        set { var changed = value != _time; _time = value; Show(); if (changed) TimeChanged?.Invoke(value); }
    }

    public static string Format(TimeOnly t) => t.ToString("t", CultureInfo.CurrentCulture);

    /// <summary>The list's entries: every half hour; with a start, the times after it labelled with the duration.</summary>
    public static IReadOnlyList<(TimeOnly Time, string Label)> Entries(TimeOnly? from)
    {
        var list = new List<(TimeOnly, string)>();
        for (var i = 0; i < 48; i++)
        {
            var t = new TimeOnly(i / 2, i % 2 * 30);
            if (from is { } f)
            {
                if (t < f) continue;
                list.Add((t, Format(t) + " (" + Duration(t - f) + ")"));
            }
            else list.Add((t, Format(t)));
        }
        return list;
    }

    public static string Duration(TimeSpan d) =>
        d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes} minutes"
        : d.TotalMinutes % 60 == 0 ? (d.TotalHours == 1 ? "1 hour" : $"{d.TotalHours:0} hours")
        : $"{d.TotalHours:0.#} hours";

    /// <summary>Reads a typed time: "8:15 AM", "8:15pm", "20:30", "8", "8p", "830".</summary>
    public static TimeOnly? Parse(string? text)
    {
        var t = (text ?? "").Trim().ToLowerInvariant().Replace(".", "");
        var paren = t.IndexOf('(');
        if (paren > 0) t = t[..paren].Trim();                                                       // "8:30 am (30 minutes)"
        if (TimeOnly.TryParse(t, CultureInfo.CurrentCulture, DateTimeStyles.None, out var parsed)) return parsed;
        var pm = t.EndsWith("pm") || t.EndsWith('p');
        var am = t.EndsWith("am") || t.EndsWith('a');
        var digits = new string(t.TrimEnd('m', 'a', 'p', ' ').Where(c => char.IsDigit(c) || c == ':').ToArray());
        int h, m = 0;
        if (digits.Contains(':'))
        {
            var parts = digits.Split(':');
            if (!int.TryParse(parts[0], out h) || parts.Length > 1 && parts[1].Length > 0 && !int.TryParse(parts[1], out m)) return null;
        }
        else if (digits.Length is 3 or 4) { h = int.Parse(digits[..^2]); m = int.Parse(digits[^2..]); }
        else if (!int.TryParse(digits, out h)) return null;
        if (pm && h < 12) h += 12;
        if (am && h == 12) h = 0;
        return h is >= 0 and < 24 && m is >= 0 and < 60 ? new TimeOnly(h, m) : null;
    }

    internal void Type(string text) { _text.Text = text; Commit(); }
    internal string Shown => _text.Text ?? "";
    internal IReadOnlyList<string> ListLabels => _list.Items.OfType<ListBoxItem>().Select(i => i.Content?.ToString() ?? "").ToList();
    internal void OpenList()
    {
        _list.Items.Clear();
        ListBoxItem? current = null;
        foreach (var (time, label) in Entries(DurationFrom?.Invoke()))
        {
            var item = new ListBoxItem { Content = label, Tag = time, Padding = new Thickness(8, 2) };
            if (time == _time || current is null && time > _time) current = item;
            _list.Items.Add(item);
        }
        _popup.IsOpen = true;
        if (current is not null) { _list.SelectedItem = current; _list.ScrollIntoView(current); }
    }

    public new bool IsEnabled { get => _text.IsEnabled; set { _text.IsEnabled = _button.IsEnabled = value; } }

    private void Commit()
    {
        if (Parse(_text.Text) is { } t) Time = t;
        else Show();
    }

    private void Show() => _text.Text = Format(_time);
}
