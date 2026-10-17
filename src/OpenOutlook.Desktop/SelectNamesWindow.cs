using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace OpenOutlook.Desktop;

/// <summary>
/// Outlook's "Select Names: Contacts" dialog (owner's screenshot 2026-10-17): Search (All columns / Name only), an Address Book choice
/// (the account's contacts, or the addresses recently written to), the list (Name, Display Name, Email Address), and To / Cc / Bcc buttons that
/// put the selected names in the boxes beside them; OK hands the three boxes to the message.
/// </summary>
public sealed class SelectNamesWindow : Window
{
    private readonly IReadOnlyList<RecipientEntry> _contacts, _recent;
    private readonly TextBox _search = new() { Width = 290, Watermark = "" };
    private readonly RadioButton _allColumns = new() { Content = "All columns", GroupName = "selectNamesSearch", IsChecked = true };
    private readonly RadioButton _nameOnly = new() { Content = "Name only", GroupName = "selectNamesSearch" };
    private readonly ComboBox _book = new() { Width = 360 };
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Multiple, Padding = new Thickness(0), Background = Themed.Brush("OlCard", Brushes.White) };
    private readonly TextBox _to = new(), _cc = new(), _bcc = new();
    private readonly Button _ok = new() { Content = "OK", MinWidth = 110, IsDefault = true, IsEnabled = false };
    private readonly string _openedFrom;

    /// <summary>The three boxes after OK; null after Cancel.</summary>
    public (string To, string Cc, string Bcc)? Result { get; private set; }

    public SelectNamesWindow(string account, IReadOnlyList<RecipientEntry> contacts, IReadOnlyList<RecipientEntry> recent, string openedFrom = "To")
    {
        _contacts = contacts.OrderBy(c => c.Shown, StringComparer.CurrentCultureIgnoreCase).ToList();
        _recent = recent.OrderByDescending(r => r.LastUsed).ToList();
        _openedFrom = openedFrom;
        Title = "Select Names: Contacts";
        Width = 920; Height = 600; MinWidth = 640; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Themed.Brush("OlCard", Brushes.White);
        Styles.Add(new Avalonia.Styling.Style(x => x.OfType<ListBoxItem>())
        { Setters = { new Avalonia.Styling.Setter(ListBoxItem.PaddingProperty, new Thickness(4, 1)), new Avalonia.Styling.Setter(ListBoxItem.MinHeightProperty, 0.0) } });

        _book.Items.Add(new ComboBoxItem { Content = $"Contacts - {account}" });
        _book.Items.Add(new ComboBoxItem { Content = $"Recent recipients - {account}" });
        _book.SelectedIndex = _contacts.Count > 0 || _recent.Count == 0 ? 0 : 1;
        _book.SelectionChanged += (_, _) => Refresh();
        _search.TextChanged += (_, _) => Refresh();
        _allColumns.IsCheckedChanged += (_, _) => Refresh();
        var clear = new Button { Content = "✕", Padding = new Thickness(8, 2) };
        ToolTip.SetTip(clear, "Clear the search");
        clear.Click += (_, _) => _search.Text = "";

        _list.ItemTemplate = new FuncDataTemplate<RecipientEntry>((e, _) => e is null ? new TextBlock() : Row(e.Shown, e.Shown, e.Address, FontWeight.Normal));
        _list.DoubleTapped += (_, _) => AddSelected(_openedFrom);
        _list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { AddSelected(_openedFrom); e.Handled = true; } };
        foreach (var box in new[] { _to, _cc, _bcc }) box.TextChanged += (_, _) => UpdateOk();

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(12, 10, 12, 6) };
        var searchSide = new StackPanel
        {
            Spacing = 4, Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { new TextBlock { Text = "Search:", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }, _allColumns, _nameOnly } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { _search, clear } },
            }
        };
        var bookSide = new StackPanel
        {
            Margin = new Thickness(24, 0, 0, 0), Spacing = 4,
            Children = { new TextBlock { Text = "Address Book:", FontWeight = FontWeight.SemiBold }, _book }
        };
        top.Children.Add(searchSide);
        Grid.SetColumn(bookSide, 1); top.Children.Add(bookSide);

        var header = Row("Name", "Display Name", "Email Address", FontWeight.Normal);
        header.Margin = new Thickness(5, 3);
        var listPanel = new DockPanel { Margin = new Thickness(12, 0, 12, 8) };
        var headerBorder = new Border { Background = Themed.Brush("OlHeader", Brushes.WhiteSmoke), BorderBrush = Themed.Brush("OlHairline", Brushes.LightGray), BorderThickness = new Thickness(1, 1, 1, 0), Child = header };
        DockPanel.SetDock(headerBorder, Dock.Top);
        listPanel.Children.Add(headerBorder);
        listPanel.Children.Add(new Border { BorderBrush = Themed.Brush("OlHairline", Brushes.LightGray), BorderThickness = new Thickness(1), Child = _list });

        var fields = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), Margin = new Thickness(12, 0, 12, 8) };
        var row = 0;
        foreach (var (label, box) in new[] { ("To", _to), ("Cc", _cc), ("Bcc", _bcc) })
        {
            var b = new Button { Content = label + " ->", MinWidth = 110, Margin = new Thickness(0, 2, 8, 2), HorizontalContentAlignment = HorizontalAlignment.Center };
            ToolTip.SetTip(b, $"Add the selected names to {label}");
            b.Click += (_, _) => AddSelected(label);
            Grid.SetRow(b, row); fields.Children.Add(b);
            box.Margin = new Thickness(0, 2);
            Grid.SetRow(box, row); Grid.SetColumn(box, 1); fields.Children.Add(box);
            row++;
        }
        var cancel = new Button { Content = "Cancel", MinWidth = 110, IsCancel = true };
        _ok.Click += (_, _) => { Result = (_to.Text ?? "", _cc.Text ?? "", _bcc.Text ?? ""); Close(); };
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 12, 12), Children = { _ok, cancel } };

        var dock = new DockPanel();
        foreach (var (c, side) in new (Control, Dock)[] { (top, Dock.Top), (buttons, Dock.Bottom), (fields, Dock.Bottom) }) { DockPanel.SetDock(c, side); dock.Children.Add(c); }
        dock.Children.Add(listPanel);
        Content = dock;
        Refresh();
        Opened += (_, _) => _search.Focus();
    }

    private static Grid Row(string name, string display, string address, FontWeight weight)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), Background = Brushes.Transparent };
        var i = 0;
        foreach (var text in new[] { name, display, address })
        {
            var t = new TextBlock { Text = text, FontWeight = weight, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0) };
            Grid.SetColumn(t, i++); g.Children.Add(t);
        }
        return g;
    }

    internal IReadOnlyList<RecipientEntry> Shown => _list.ItemsSource?.OfType<RecipientEntry>().ToList() ?? [];
    internal void Search(string text, bool nameOnly = false) { _nameOnly.IsChecked = nameOnly; _allColumns.IsChecked = !nameOnly; _search.Text = text; Refresh(); }
    internal void ChooseBook(int index) { _book.SelectedIndex = index; Refresh(); }
    internal void Select(params RecipientEntry[] entries) { _list.SelectedItems!.Clear(); foreach (var e in entries) _list.SelectedItems.Add(e); }
    internal string Field(string label) => (label switch { "Cc" => _cc, "Bcc" => _bcc, _ => _to }).Text ?? "";
    internal void Confirm() { if (AnyChosen) { Result = (_to.Text ?? "", _cc.Text ?? "", _bcc.Text ?? ""); Close(); } }

    private void Refresh()
    {
        var source = _book.SelectedIndex == 1 ? _recent : _contacts;
        var q = (_search.Text ?? "").Trim();
        var nameOnly = _nameOnly.IsChecked == true;
        _list.ItemsSource = (q.Length == 0 ? source : source.Where(e => RecipientDirectory.Matches(e, q, nameOnly) ||
            !nameOnly && e.Address.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>Puts the selected names in To, Cc or Bcc (Outlook's buttons beside the boxes).</summary>
    internal void AddSelected(string label)
    {
        var chosen = _list.SelectedItems?.OfType<RecipientEntry>().Select(e => e.Address).ToList() ?? [];
        if (chosen.Count == 0) return;
        var box = label switch { "Cc" => _cc, "Bcc" => _bcc, _ => _to };
        box.Text = RecipientDirectory.Append(box.Text, chosen);
        UpdateOk();
    }

    private bool AnyChosen => new[] { _to, _cc, _bcc }.Any(b => !string.IsNullOrWhiteSpace(b.Text));
    private void UpdateOk() => _ok.IsEnabled = AnyChosen;
}
