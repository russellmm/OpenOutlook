using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace OpenOutlook.Desktop;

/// <summary>
/// Outlook's Reminders window (docs/reminders-plan.md section 4): the selected reminder's subject, start and location on top, every due reminder
/// with its "Due in" text below, then Dismiss All / Open Item / Dismiss and "Click Snooze to be reminded again in:" with Snooze. Closing it with
/// X only hides it until the next reminder comes due; nothing is dismissed.
/// </summary>
public sealed class RemindersWindow : Window
{
    public static readonly (string Label, TimeSpan Span)[] SnoozeChoices =
    [
        ("5 minutes", TimeSpan.FromMinutes(5)), ("10 minutes", TimeSpan.FromMinutes(10)), ("15 minutes", TimeSpan.FromMinutes(15)),
        ("30 minutes", TimeSpan.FromMinutes(30)), ("1 hour", TimeSpan.FromHours(1)), ("2 hours", TimeSpan.FromHours(2)),
        ("4 hours", TimeSpan.FromHours(4)), ("1 day", TimeSpan.FromDays(1)), ("1 week", TimeSpan.FromDays(7)),
    ];

    private readonly Func<IReadOnlyList<ReminderItem>, Task> _dismiss;
    private readonly Func<IReadOnlyList<ReminderItem>, TimeSpan, Task> _snooze;
    private readonly Action<ReminderItem> _open;
    private readonly Func<int, Task<bool>> _confirmDismissAll;
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Multiple };
    private readonly TextBlock _subject = new() { FontSize = 15, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _when = new() { FontSize = 12 };
    private readonly TextBlock _where = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ComboBox _snoozeFor = new() { MinWidth = 180, ItemsSource = SnoozeChoices.Select(c => c.Label).ToArray(), SelectedIndex = 0 };
    private readonly Button _dismissAll = new() { Content = "Dismiss All", Padding = new Thickness(14, 4) };
    private readonly Button _openItem = new() { Content = "Open Item", Padding = new Thickness(14, 4) };
    private readonly Button _dismissOne = new() { Content = "Dismiss", Padding = new Thickness(14, 4) };
    private readonly Button _snoozeButton = new() { Content = "Snooze", Padding = new Thickness(14, 4) };
    private IReadOnlyList<ReminderItem> _items = [];
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    /// <summary>True when the user closed the window (X); the owner shows it again when a new reminder comes due.</summary>
    public bool HiddenByUser { get; private set; }

    public RemindersWindow(Func<IReadOnlyList<ReminderItem>, Task> dismiss, Func<IReadOnlyList<ReminderItem>, TimeSpan, Task> snooze,
        Action<ReminderItem> open, Func<int, Task<bool>>? confirmDismissAll = null)
    {
        _dismiss = dismiss; _snooze = snooze; _open = open;
        _confirmDismissAll = confirmDismissAll ?? AskDismissAllAsync;
        Width = 520; Height = 420; MinWidth = 400; MinHeight = 300;
        Topmost = true;                                                               // like Outlook: in front of other programs until dealt with
        ShowActivated = false;                                                        // but it does not take the keyboard from what the user is typing
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Themed.Brush("OlCard", Brushes.White);
        ShowInTaskbar = true;

        _list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<ReminderItem>((item, _) => Row(item));
        _list.SelectionChanged += (_, _) => ShowSelected();
        _list.DoubleTapped += (_, _) => { if (Selected().FirstOrDefault() is { } item) _open(item); };
        _list.KeyDown += (_, e) => { if (e.Key == Key.Enter && Selected().FirstOrDefault() is { } item) { _open(item); e.Handled = true; } };
        _dismissAll.Click += async (_, _) => await DismissAllAsync();
        _dismissOne.Click += async (_, _) => await DismissSelectedAsync();
        _openItem.Click += (_, _) => { if (Selected().FirstOrDefault() is { } item) _open(item); };
        _snoozeButton.Click += async (_, _) => await SnoozeSelectedAsync();
        Closing += (_, e) =>
        {
            if (e.CloseReason is WindowCloseReason.OwnerWindowClosing or WindowCloseReason.ApplicationShutdown || _allowClose) return;
            e.Cancel = true;                                                          // X = hide until the next reminder, not dismiss
            HiddenByUser = true;
            Hide();
        };

        var header = new StackPanel
        {
            Margin = new Thickness(14, 12, 14, 8), Spacing = 3,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "\U0001F514", FontSize = 18, VerticalAlignment = VerticalAlignment.Center }, _subject } },
                _when, _where
            }
        };
        var columns = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,150"), Margin = new Thickness(14, 0, 14, 0), Background = Themed.Brush("OlHeader", Brushes.WhiteSmoke),
            Children =
            {
                new TextBlock { Text = "Subject", FontSize = 12, Margin = new Thickness(12, 3) },
                Column(new TextBlock { Text = "Due in", FontSize = 12, Margin = new Thickness(0, 3) }, 1)
            }
        };
        var listBorder = new Border
        {
            Margin = new Thickness(14, 0, 14, 8), BorderBrush = Themed.Brush("OlHairline", Brushes.LightGray), BorderThickness = new Thickness(1),
            Child = _list
        };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(14, 0, 14, 8),
            Children = { _dismissAll, new Border { Width = 40 }, _openItem, _dismissOne }
        };
        var snoozeRow = new DockPanel { Margin = new Thickness(14, 0, 14, 14), LastChildFill = false };
        var snoozeLabel = new StackPanel
        {
            Spacing = 3, Children = { new TextBlock { Text = "Click Snooze to be reminded again in:", FontSize = 12 }, _snoozeFor }
        };
        DockPanel.SetDock(snoozeLabel, Dock.Left); DockPanel.SetDock(_snoozeButton, Dock.Right);
        _snoozeButton.VerticalAlignment = VerticalAlignment.Bottom;
        snoozeRow.Children.Add(snoozeLabel); snoozeRow.Children.Add(_snoozeButton);

        var dock = new DockPanel();
        foreach (var (control, side) in new (Control, Dock)[] { (header, Dock.Top), (columns, Dock.Top), (snoozeRow, Dock.Bottom), (actions, Dock.Bottom) })
        {
            DockPanel.SetDock(control, side);
            dock.Children.Add(control);
        }
        dock.Children.Add(listBorder);
        Content = dock;
        SetItems([], DateTimeOffset.UtcNow);
        var tick = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        tick.Tick += (_, _) => { if (IsVisible) SetItems(_items, DateTimeOffset.UtcNow); };          // "Due in" keeps counting
        Opened += (_, _) => tick.Start();
        Closed += (_, _) => tick.Stop();
    }

    private bool _allowClose;

    /// <summary>Closes for good (the main window is closing).</summary>
    public void CloseForGood() { _allowClose = true; Close(); }

    /// <summary>The reminders shown, newest due first. Keeps the selection where it can; selects the first otherwise.</summary>
    public void SetItems(IReadOnlyList<ReminderItem> items, DateTimeOffset now)
    {
        var keep = Selected().Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        _items = items; _now = now;
        _list.ItemsSource = items.ToList();                                          // a new list re-renders the "Due in" texts
        foreach (var item in items.Where(i => keep.Contains(i.Key))) _list.SelectedItems!.Add(item);
        if (_list.SelectedItems!.Count == 0 && items.Count > 0) _list.SelectedIndex = 0;
        Title = items.Count == 1 ? "1 Reminder" : $"{items.Count} Reminders";
        _dismissAll.IsEnabled = items.Count > 0;
        ShowSelected();
    }

    internal IReadOnlyList<ReminderItem> Items => _items;
    internal IReadOnlyList<ReminderItem> Selected() => _list.SelectedItems?.OfType<ReminderItem>().ToList() ?? [];
    internal void Select(params ReminderItem[] items) { _list.SelectedItems!.Clear(); foreach (var i in items) _list.SelectedItems.Add(i); }
    internal void ChooseSnooze(int index) => _snoozeFor.SelectedIndex = index;
    internal string HeaderSubject => _subject.Text ?? "";
    internal string HeaderWhen => _when.Text ?? "";

    internal async Task DismissSelectedAsync()
    {
        var chosen = Selected();
        if (chosen.Count > 0) await _dismiss(chosen);
    }

    internal async Task DismissAllAsync()
    {
        if (_items.Count == 0) return;
        if (_items.Count > 1 && !await _confirmDismissAll(_items.Count)) return;
        await _dismiss(_items.ToList());
    }

    internal async Task SnoozeSelectedAsync()
    {
        var chosen = Selected();
        if (chosen.Count > 0) await _snooze(chosen, SnoozeChoices[Math.Clamp(_snoozeFor.SelectedIndex, 0, SnoozeChoices.Length - 1)].Span);
    }

    /// <summary>The "Due in" text of a shown reminder (as the row shows it).</summary>
    internal string DueText(ReminderItem item) => ReminderSchedule.DueText(item.EventStart, _now);

    private void ShowSelected()
    {
        var chosen = Selected();
        var one = chosen.Count == 1 ? chosen[0] : null;
        _subject.Text = one?.Subject ?? (chosen.Count > 1 ? $"{chosen.Count} reminders are selected" : _items.Count == 0 ? "No reminders" : "");
        _when.Text = one is null ? "" : "Start time: " + one.EventStart.ToLocalTime().ToString("dddd, MMMM d, yyyy h:mm tt", System.Globalization.CultureInfo.CurrentCulture);
        _where.Text = one is { Location.Length: > 0 } ? "Location: " + one.Location : "";
        _openItem.IsEnabled = one is not null;
        _dismissOne.IsEnabled = _snoozeButton.IsEnabled = chosen.Count > 0;
    }

    private Control Row(ReminderItem? item)
    {
        if (item is null) return new TextBlock();                                  // the template is also built while the list is emptied
        var overdue = _now >= item.EventStart;
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,150"), Background = Brushes.Transparent,
            Children =
            {
                new TextBlock { Text = item.Subject, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center },
                Column(new TextBlock
                {
                    Text = ReminderSchedule.DueText(item.EventStart, _now), FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = overdue ? Themed.Brush("OlDanger", new SolidColorBrush(Color.FromRgb(0xA4, 0x26, 0x2C))) : Themed.Brush("OlText", Brushes.Black)
                }, 1)
            }
        };
    }

    private static Control Column(Control control, int column) { Grid.SetColumn(control, column); return control; }

    private async Task<bool> AskDismissAllAsync(int count)
    {
        var dialog = new Window
        {
            Title = "Dismiss All", Width = 380, Height = 150, CanResize = false, Topmost = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Themed.Brush("OlCard", Brushes.White)
        };
        var yes = new Button { Content = "Yes", IsDefault = true, Padding = new Thickness(18, 4) };
        var no = new Button { Content = "No", IsCancel = true, Padding = new Thickness(18, 4) };
        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16), Spacing = 14, Children =
            {
                new TextBlock { Text = $"Dismiss all {count} reminders?", TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { yes, no } }
            }
        };
        return await dialog.ShowDialog<bool>(this);
    }
}
