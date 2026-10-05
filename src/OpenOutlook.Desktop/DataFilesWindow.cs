using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OpenOutlook.Mirror;

namespace OpenOutlook.Desktop;

/// <summary>One row of the Data Files screen: a connected account and its local mailbox copy (a PST file).</summary>
public sealed record DataFileRow(string AccountId, string Address, string Kind, string Path, long SizeBytes, DateTime? LastSync, string Status, MirrorAccountSettings Settings, bool Supported);

/// <summary>Account Settings > Data Files: where each mailbox copy lives, how much it keeps, and sync / relocate / open-folder commands.</summary>
public sealed class DataFilesWindow : Window
{
    private readonly Func<IReadOnlyList<DataFileRow>> _rows;
    private readonly Func<string, Task> _syncNow;
    private readonly Func<string, Task<string?>> _changeLocation;
    private readonly Action<string> _openLocation;
    private readonly Action<string, MirrorAccountSettings> _saveSettings;
    private readonly StackPanel _list = new() { Spacing = 10 };
    private readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

    public DataFilesWindow(Func<IReadOnlyList<DataFileRow>> rows, Func<string, Task> syncNow, Func<string, Task<string?>> changeLocation,
        Action<string> openLocation, Action<string, MirrorAccountSettings> saveSettings)
    {
        _rows = rows; _syncNow = syncNow; _changeLocation = changeLocation; _openLocation = openLocation; _saveSettings = saveSettings;
        Title = "Account Settings - Data Files";
        Width = 920; Height = 520; MinWidth = 700; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var heading = new TextBlock { Text = "Data Files", FontSize = 20, FontWeight = FontWeight.SemiBold };
        var intro = new TextBlock
        {
            Text = "Each mailbox keeps a copy in a PST file on this computer, so mail opens instantly and stays readable offline. The server remains the original; changes made on the server arrive on the next sync.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12)
        };
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(18, 5) };
        close.Click += (_, _) => Close();
        var scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        var bottom = new StackPanel { Spacing = 8, Margin = new Thickness(0, 10, 0, 0), Children = { _note, close } };
        var dock = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(heading, Dock.Top); DockPanel.SetDock(intro, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(heading); dock.Children.Add(intro); dock.Children.Add(bottom); dock.Children.Add(scroll);
        Content = dock;
        Refresh();
    }

    /// <summary>Reloads the rows (called after a sync, a relocation or a settings change).</summary>
    public void Refresh()
    {
        _list.Children.Clear();
        var rows = _rows();
        if (rows.Count == 0)
            _list.Children.Add(new TextBlock { Text = "No mailbox accounts are connected. Use Add Account on the File > Info page.", TextWrapping = TextWrapping.Wrap });
        foreach (var row in rows) _list.Children.Add(Card(row));
    }

    internal void SetNote(string text) => _note.Text = text;

    private Control Card(DataFileRow row)
    {
        var title = new TextBlock { Text = $"{row.Address}  ({row.Kind})", FontWeight = FontWeight.SemiBold, FontSize = 15 };
        var path = new TextBlock { Text = row.Supported ? row.Path : "Mailbox copies for this account type are not available yet.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray };
        var info = new TextBlock
        {
            Text = row.Supported ? $"{FormatSize(row.SizeBytes)}   |   last synchronised: {(row.LastSync is { } l ? l.ToLocalTime().ToString("g") : "never")}   |   {row.Status}" : "",
            TextWrapping = TextWrapping.Wrap
        };
        var stack = new StackPanel { Spacing = 6, Children = { title, path, info } };
        if (row.Supported)
        {
            var enabled = new CheckBox { Content = "Keep a copy on this computer", IsChecked = row.Settings.Enabled };
            var months = new ComboBox { Width = 150 };
            var choices = new (string Label, int Months)[] { ("All mail", 0), ("12 months", 12), ("6 months", 6), ("3 months", 3), ("1 month", 1) };
            foreach (var c in choices) months.Items.Add(new ComboBoxItem { Content = c.Label, Tag = c.Months });
            months.SelectedIndex = Math.Max(0, Array.FindIndex(choices, c => c.Months == row.Settings.KeepMonths));
            var cap = new ComboBox { Width = 130 };
            var caps = new (string Label, long Bytes)[] { ("5 MB", 5L << 20), ("25 MB", 25L << 20), ("100 MB", 100L << 20) };
            foreach (var c in caps) cap.Items.Add(new ComboBoxItem { Content = c.Label, Tag = c.Bytes });
            cap.SelectedIndex = Math.Max(0, Array.FindIndex(caps, c => c.Bytes == row.Settings.MaxAttachmentBytes));
            void Save()
            {
                var m = (months.SelectedItem as ComboBoxItem)?.Tag is int mm ? mm : 12;
                var b = (cap.SelectedItem as ComboBoxItem)?.Tag is long bb ? bb : 25L << 20;
                _saveSettings(row.AccountId, row.Settings with { Enabled = enabled.IsChecked == true, KeepMonths = m, MaxAttachmentBytes = b });
            }
            enabled.IsCheckedChanged += (_, _) => Save();
            months.SelectionChanged += (_, _) => Save();
            cap.SelectionChanged += (_, _) => Save();
            var sync = new Button { Content = "Sync now" };
            sync.Click += async (_, _) => { sync.IsEnabled = false; try { await _syncNow(row.AccountId); } finally { sync.IsEnabled = true; Refresh(); } };
            var relocate = new Button { Content = "Change location…" };
            relocate.Click += async (_, _) => { var note = await _changeLocation(row.AccountId); if (note is not null) _note.Text = note; Refresh(); };
            var open = new Button { Content = "Open file location" };
            open.Click += (_, _) => _openLocation(row.AccountId);
            stack.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 10,
                Children = { enabled, new TextBlock { Text = "Keep mail from the last", VerticalAlignment = VerticalAlignment.Center }, months,
                    new TextBlock { Text = "Largest attachment", VerticalAlignment = VerticalAlignment.Center }, cap }
            });
            stack.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { sync, relocate, open } });
        }
        return new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(12), Child = stack };
    }

    internal static string FormatSize(long bytes) =>
        bytes <= 0 ? "no file yet" : bytes < 1 << 20 ? $"{bytes / 1024.0:0.#} KB" : bytes < 1L << 30 ? $"{bytes / 1048576.0:0.#} MB" : $"{bytes / 1073741824.0:0.##} GB";
}
