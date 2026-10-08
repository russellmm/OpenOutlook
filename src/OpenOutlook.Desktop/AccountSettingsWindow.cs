using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using OpenOutlook.Auth;
using OpenOutlook.JunkCleaner;
using OpenOutlook.Mirror;

namespace OpenOutlook.Desktop;

/// <summary>One row of the Data Files tab: the local copy of a mailbox, or an Outlook data file (PST) that was opened.</summary>
public sealed record DataFileRow(string Name, string Path, string Kind, long SizeBytes, string Status, string? AccountId = null, DateTime? LastSync = null,
    MirrorAccountSettings? Settings = null, bool IsMirror = false, bool Available = true);

/// <summary>What the Account Settings dialog needs from the application (implemented by the main window).</summary>
public interface IAccountSettingsHost
{
    IReadOnlyList<ConnectedAccount> Accounts();
    string? DefaultAccountId { get; }
    void SetDefaultAccount(string accountId);
    Task AddAccountAsync();
    Task RepairAccountAsync(ConnectedAccount account);
    Task<string?> RemoveAccountAsync(ConnectedAccount account, Window owner);
    IReadOnlyList<DataFileRow> DataFiles();
    Task<string?> AddDataFileAsync(Window owner);
    Task<string?> AddDataFileByPathAsync(string path);
    Task<string?> CreateDataFileAsync(Window owner);
    Task<string?> RemoveDataFileAsync(DataFileRow row);
    void OpenFileLocation(DataFileRow row);
    Task SyncNowAsync(string accountId);
    Task<string?> ChangeLocationAsync(string accountId, Window owner);
    void SaveMirrorSettings(string accountId, MirrorAccountSettings settings);
    IReadOnlyList<ConnectedAccount> JunkAccounts();
    JunkCleanerAccountSettings JunkSettings(string accountId);
    void SaveJunkSettings(JunkCleanerAccountSettings settings);
    Task<string?> CleanJunkNowAsync(string accountId, Window owner);
    Task<string?> ImportJunkConfigAsync(string accountId, Window owner);
    Task<string?> ExportJunkConfigAsync(string accountId, Window owner);
    IReadOnlyList<string> JunkLog();
}

/// <summary>Theme-aware lookups for the dialogs that are built in code (the XAML windows use DynamicResource; here the current theme's value is read when the control is made).</summary>
internal static class Themed
{
    public static IBrush Brush(string key, IBrush fallback) =>
        Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var v) && v is IBrush b ? b : fallback;

    public static Control Icon(string key, double size = 16)
    {
        if (Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var t) && t is Avalonia.Controls.Templates.IControlTemplate template)
            return new ContentControl { Template = (Avalonia.Controls.Templates.IControlTemplate)template, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        return new Border { Width = size, Height = size };
    }
}

/// <summary>Account Settings, laid out like Outlook's dialog: an Email tab (the connected accounts) and a Data Files tab (mailbox copies and opened PST files).</summary>
public sealed partial class AccountSettingsWindow : Window
{
    private readonly IAccountSettingsHost _host;
    private readonly ListBox _emailList = new() { SelectionMode = SelectionMode.Single, Classes = { "olList" }, BorderThickness = new Thickness(0) };
    private readonly ListBox _fileList = new() { SelectionMode = SelectionMode.Single, Classes = { "olList" }, BorderThickness = new Thickness(0) };
    private readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13, MinHeight = 22 };
    private readonly TabControl _tabs = new() { Classes = { "olTabs" } };
    private IReadOnlyList<ConnectedAccount> _accounts = [];
    private IReadOnlyList<DataFileRow> _files = [];

    public AccountSettingsWindow(IAccountSettingsHost host, bool startOnDataFiles = false, bool startOnJunk = false)
    {
        _host = host;
        Title = "Account Settings";
        Width = 960; Height = 620; MinWidth = 760; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Themed.Brush("OlCard", Brushes.White);
        _note.Foreground = Themed.Brush("OlTextSecondary", Brushes.Gray);

        var heading = new StackPanel
        {
            Margin = new Thickness(20, 16, 20, 4),
            Children =
            {
                new TextBlock { Text = "Account Settings", FontSize = 22, FontWeight = FontWeight.SemiBold, Foreground = Themed.Brush("OlText", Brushes.Black) },
                new TextBlock { Text = "Your connected accounts, and the files that hold your mail on this computer.", FontSize = 13, Margin = new Thickness(0, 2, 0, 0), Foreground = Themed.Brush("OlTextSecondary", Brushes.Gray) }
            }
        };
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(28, 6), IsDefault = true };
        close.Click += (_, _) => Close();

        _tabs.Items.Add(new TabItem { Header = "Email", Content = BuildEmailTab() });
        _tabs.Items.Add(new TabItem { Header = "Data Files", Content = BuildDataFilesTab() });
        _tabs.Items.Add(new TabItem { Header = "Junk Cleaner", Content = BuildJunkTab() });
        _tabs.SelectedIndex = startOnJunk ? 2 : startOnDataFiles ? 1 : 0;

        var bottom = new Border
        {
            BorderBrush = Themed.Brush("OlHairline", Brushes.LightGray), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(20, 10),
            Child = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { _note, Place(close, 1) } }
        };
        var dock = new DockPanel();
        DockPanel.SetDock(heading, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
        _tabs.Margin = new Thickness(12, 4, 12, 8);
        dock.Children.Add(heading); dock.Children.Add(bottom); dock.Children.Add(_tabs);
        Content = dock;
        Refresh();
    }

    private static T Place<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; }

    // ---- building blocks -------------------------------------------------------------------------------------------------------------------------

    /// <summary>A flat toolbar command: icon and caption side by side, like the ribbon's small commands.</summary>
    private static Button Tool(string caption, string icon, string tip, bool planned = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children = { Themed.Icon(icon, 16), new TextBlock { Text = caption, FontSize = 13, VerticalAlignment = VerticalAlignment.Center } } };
        var b = new Button { Classes = { "olSmall" }, Content = content, Padding = new Thickness(9, 5), Margin = new Thickness(0, 0, 2, 0) };
        ToolTip.SetTip(b, planned ? "To be implemented" : tip);
        if (planned) b.Opacity = 0.7;
        return b;
    }

    private static Border Toolbar(params Control[] tools) =>
        new()
        {
            Padding = new Thickness(4, 4), BorderBrush = Themed.Brush("OlHairline", Brushes.LightGray), BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { } }.With(tools)
        };

    private static Grid Row(params (Control Cell, GridLength Width)[] cells)
    {
        var grid = new Grid { MinHeight = 44 };
        for (var i = 0; i < cells.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(cells[i].Width));
            Grid.SetColumn(cells[i].Cell, i);
            grid.Children.Add(cells[i].Cell);
        }
        return grid;
    }

    private static TextBlock Cell(string text, bool bold = false, bool gray = false, double size = 13)
    {
        var t = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, FontSize = size, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0) };
        t.Foreground = Themed.Brush(gray ? "OlTextSecondary" : "OlText", gray ? Brushes.Gray : Brushes.Black);
        ToolTip.SetTip(t, text);
        return t;
    }

    private static Control Header(params (string Text, GridLength Width)[] cols) =>
        new Border
        {
            Background = Themed.Brush("OlGroupBand", Brushes.WhiteSmoke), BorderBrush = Themed.Brush("OlHairline", Brushes.LightGray), BorderThickness = new Thickness(0, 0, 0, 1),
            Child = Row(cols.Select(c => ((Control)new TextBlock { Text = c.Text, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Themed.Brush("OlTextSecondary", Brushes.Gray), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0) }, c.Width)).ToArray())
        };

    private static Border Frame(Control header, Control list) =>
        new()
        {
            BorderBrush = Themed.Brush("OlStrongBorder", Brushes.Gray), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), ClipToBounds = true,
            Background = Themed.Brush("OlField", Brushes.White),
            Child = new DockPanel().With(Dock.Top, header).With(list)
        };

    /// <summary>The check mark in front of the default account (a drawn path: the check-mark characters turn into colour emoji on some systems).</summary>
    private static Control DefaultMark(bool isDefault) =>
        new Border
        {
            Width = 22, VerticalAlignment = VerticalAlignment.Center, Tag = isDefault ? "default" : null,
            Child = isDefault
                ? new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M3,9 L7.5,13.5 L15,4.5"), Stroke = Themed.Brush("OlAccent", Brushes.SteelBlue), StrokeThickness = 2, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Center }
                : null
        };

    private static Control StatusDot(string status)
    {
        var s = status.ToLowerInvariant();
        var color = s.Length == 0 ? null : s.Contains("problem") || s.Contains("could not") || s.Contains("sign in") || s.Contains("cannot") ? Brushes.Firebrick
            : s.Contains("offline") ? Brushes.DarkOrange : s.Contains("synchronising") || s.Contains("waiting") ? Brushes.SteelBlue : s.Contains("turned off") || s.Contains("read-only") ? Brushes.Gray : Brushes.SeaGreen;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0) };
        if (color is not null) panel.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = color, VerticalAlignment = VerticalAlignment.Center });
        var text = new TextBlock { Text = status, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Foreground = Themed.Brush("OlTextSecondary", Brushes.Gray) };
        ToolTip.SetTip(text, status);
        panel.Children.Add(text);
        return panel;
    }

    // ---- Email tab --------------------------------------------------------------------------------------------------------------------------------

    private static readonly (string Text, GridLength Width)[] EmailColumns =
        [("", new GridLength(26)), ("Name", new GridLength(3, GridUnitType.Star)), ("Type", new GridLength(3, GridUnitType.Star))];

    private Control BuildEmailTab()
    {
        var add = Tool("New…", "OlIconPeople", "Connect another Microsoft or Gmail account");
        var repair = Tool("Repair…", "OlIconRefresh", "Sign in again for the selected account");
        var change = Tool("Change…", "OlIconGear", "", planned: true);
        var makeDefault = Tool("Set as Default", "OlIconFlag", "New messages are sent from the default account unless you choose another");
        var remove = Tool("Remove", "OlIconDelete", "Disconnect the selected account from this computer");
        var up = Tool("", "OlIconSortUp", "", planned: true);
        var down = Tool("", "OlIconChevronDown", "", planned: true);
        add.Click += async (_, _) => { await _host.AddAccountAsync(); Refresh(); };
        repair.Click += async (_, _) => { if (SelectedAccount() is { } a) { await _host.RepairAccountAsync(a); Refresh(); } else _note.Text = "Select an account first."; };
        change.Click += (_, _) => _note.Text = "Changing account details is to be implemented. Use Repair… to sign in again.";
        makeDefault.Click += (_, _) => { if (SelectedAccount() is { } a) { _host.SetDefaultAccount(a.AccountId); Refresh(); _note.Text = $"{a.DisplayAddress} is now the default account for new messages."; } else _note.Text = "Select an account first."; };
        remove.Click += async (_, _) =>
        {
            if (SelectedAccount() is not { } a) { _note.Text = "Select an account first."; return; }
            var result = await _host.RemoveAccountAsync(a, this);
            if (result is not null) _note.Text = result;
            Refresh();
        };
        up.Click += (_, _) => _note.Text = "Reordering accounts is to be implemented.";
        down.Click += (_, _) => _note.Text = "Reordering accounts is to be implemented.";
        var hint = new TextBlock { Text = "Select an account to repair its sign-in, make it the default, or remove it.", FontSize = 12, Margin = new Thickness(6, 8, 6, 6), Foreground = Themed.Brush("OlTextSecondary", Brushes.Gray) };
        var dock = new DockPanel { Margin = new Thickness(4, 6) };
        var toolbar = Toolbar(add, repair, change, makeDefault, remove, up, down);
        DockPanel.SetDock(toolbar, Dock.Top); DockPanel.SetDock(hint, Dock.Top);
        dock.Children.Add(toolbar); dock.Children.Add(hint); dock.Children.Add(Frame(Header(EmailColumns), _emailList));
        return dock;
    }

    private ConnectedAccount? SelectedAccount() => (_emailList.SelectedItem as ListBoxItem)?.Tag as ConnectedAccount;

    // ---- Data Files tab ---------------------------------------------------------------------------------------------------------------------------

    private static readonly (string Text, GridLength Width)[] FileColumns =
        [("", new GridLength(34)), ("Name", new GridLength(2.4, GridUnitType.Star)), ("Location", new GridLength(4, GridUnitType.Star)), ("Size", new GridLength(1, GridUnitType.Star)), ("Status", new GridLength(2.6, GridUnitType.Star))];

    private Control BuildDataFilesTab()
    {
        var add = Tool("Add…", "OlIconOpenArchive", "Open an Outlook data file (.pst) and show it in the folder list");
        var settings = Tool("Settings…", "OlIconGear", "How much of the mailbox the copy keeps, sync, and where the file is");
        var makeDefault = Tool("Set as Default", "OlIconFlag", "", planned: true);
        var remove = Tool("Remove", "OlIconDetach", "Close the file in OpenOutlook (the file itself is never deleted)");
        var create = Tool("New…", "OlIconOpenArchive", "Create a new, empty Outlook data file (.pst) and show it in the folder list");
        create.Click += async (_, _) => { var note = await _host.CreateDataFileAsync(this); if (note is not null) _note.Text = note; Refresh(); };
        var byPath = Tool("Add by path…", "OlIconOpenArchive", "Type or paste a path such as X:\\email\\old.pst or \\\\server\\share\\mail.pst, for a network drive the file dialog does not show");
        byPath.Click += async (_, _) =>
        {
            var path = await AskForPathAsync();
            if (string.IsNullOrWhiteSpace(path)) return;
            _note.Text = await _host.AddDataFileByPathAsync(path) ?? "";
            Refresh();
        };
        var open = Tool("Open File Location…", "OlIconOpenExport", "Show the folder that contains the data file");
        add.Click += async (_, _) => { var note = await _host.AddDataFileAsync(this); if (note is not null) _note.Text = note; Refresh(); };
        settings.Click += async (_, _) => await ShowSettingsAsync();
        makeDefault.Click += (_, _) => _note.Text = "Choosing a default data file is to be implemented.";
        remove.Click += async (_, _) =>
        {
            if (SelectedFile() is not { } f) { _note.Text = "Select a data file first."; return; }
            var note = await _host.RemoveDataFileAsync(f);
            if (note is not null) _note.Text = note;
            Refresh();
        };
        open.Click += (_, _) => { if (SelectedFile() is { } f) _host.OpenFileLocation(f); else _note.Text = "Select a data file first."; };
        _fileList.DoubleTapped += async (_, _) => await ShowSettingsAsync();
        var hint = new TextBlock
        {
            Text = "Select a data file, then click Settings for more details or Open File Location to show the folder that contains it.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(6, 8, 6, 6), Foreground = Themed.Brush("OlTextSecondary", Brushes.Gray)
        };
        var dock = new DockPanel { Margin = new Thickness(4, 6) };
        var toolbar = Toolbar(create, add, byPath, settings, makeDefault, remove, open);
        DockPanel.SetDock(toolbar, Dock.Top); DockPanel.SetDock(hint, Dock.Top);
        dock.Children.Add(toolbar); dock.Children.Add(hint); dock.Children.Add(Frame(Header(FileColumns), _fileList));
        return dock;
    }

    /// <summary>A one-line prompt for a file path; returns null when cancelled.</summary>
    private async Task<string?> AskForPathAsync()
    {
        var box = new TextBox { Watermark = "X:\\email\\archive.pst   or   \\\\server\\share\\archive.pst", MinWidth = 520 };
        var ok = new Button { Content = "Open", IsDefault = true, Padding = new Thickness(22, 5) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(22, 5) };
        var dialog = new Window
        {
            Title = "Add a data file by path", SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Themed.Brush("OlCard", Brushes.White), CanResize = false
        };
        ok.Click += (_, _) => dialog.Close(box.Text);
        cancel.Click += (_, _) => dialog.Close(null);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Path of the .pst file (network drives and \\\\server\\share paths work):", FontSize = 13 }, box,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } }
            }
        };
        return await dialog.ShowDialog<string?>(this);
    }

    private DataFileRow? SelectedFile() => (_fileList.SelectedItem as ListBoxItem)?.Tag as DataFileRow;

    private async Task ShowSettingsAsync()
    {
        if (SelectedFile() is not { } f) { _note.Text = "Select a data file first."; return; }
        var dialog = new DataFileSettingsDialog(_host, f);
        await dialog.ShowDialog(this);
        Refresh();
        if (dialog.Note is not null) _note.Text = dialog.Note;
    }

    // ---- refresh ----------------------------------------------------------------------------------------------------------------------------------

    /// <summary>Reloads both lists from the application (after a sign-in, a removal, a sync or a relocation).</summary>
    public void Refresh()
    {
        RefreshJunkAccounts();
        var selectedAccount = SelectedAccount()?.AccountId;
        var selectedFile = SelectedFile()?.Path;
        _accounts = _host.Accounts();
        _emailList.Items.Clear();
        var def = _host.DefaultAccountId ?? _accounts.FirstOrDefault()?.AccountId;
        foreach (var a in _accounts)
        {
            var isDefault = a.AccountId == def;
            var type = a.Provider == OAuthProvider.Google ? "Gmail (send and receive)" : "Microsoft mail (send and receive)";
            var item = new ListBoxItem
            {
                Tag = a,
                Content = Row((DefaultMark(isDefault), EmailColumns[0].Width), (Cell(a.DisplayAddress, bold: isDefault), EmailColumns[1].Width), (Cell(type, gray: true), EmailColumns[2].Width))
            };
            _emailList.Items.Add(item);
            if (a.AccountId == selectedAccount) _emailList.SelectedItem = item;
        }
        if (_accounts.Count == 0) _emailList.Items.Add(new ListBoxItem { Content = new TextBlock { Text = "No accounts are connected. Click New… to add one.", Margin = new Thickness(12, 14), Foreground = Themed.Brush("OlTextSecondary", Brushes.Gray) }, IsHitTestVisible = false });
        _files = _host.DataFiles();
        _fileList.Items.Clear();
        foreach (var f in _files)
        {
            var icon = Themed.Icon(f.IsMirror ? "OlIconMail" : "OlIconOpenArchive", 20);
            icon.Margin = new Thickness(8, 0, 4, 0);
            var item = new ListBoxItem
            {
                Tag = f,
                Content = Row((icon, FileColumns[0].Width), (Cell(f.Name, bold: f.IsMirror), FileColumns[1].Width), (Cell(f.Available ? f.Path : "Not available yet", gray: true), FileColumns[2].Width),
                    (Cell(f.SizeBytes > 0 ? FormatSize(f.SizeBytes) : "", gray: true), FileColumns[3].Width), (StatusDot(f.Status), FileColumns[4].Width))
            };
            ToolTip.SetTip(item, f.Kind);
            _fileList.Items.Add(item);
            if (f.Path == selectedFile) _fileList.SelectedItem = item;
        }
    }

    internal static string FormatSize(long bytes) =>
        bytes <= 0 ? "no file yet" : bytes < 1 << 20 ? $"{bytes / 1024.0:0.#} KB" : bytes < 1L << 30 ? $"{bytes / 1048576.0:0.#} MB" : $"{bytes / 1073741824.0:0.##} GB";
}

internal static class LayoutExtensions
{
    public static DockPanel With(this DockPanel panel, Dock dock, Control child) { DockPanel.SetDock(child, dock); panel.Children.Add(child); return panel; }
    public static DockPanel With(this DockPanel panel, Control child) { panel.Children.Add(child); return panel; }
}

/// <summary>The Settings… dialog of one data file: for a mailbox copy its keep window, attachment limit, sync and location; for an opened file its details.</summary>
public sealed class DataFileSettingsDialog : Window
{
    public string? Note { get; private set; }

    public DataFileSettingsDialog(IAccountSettingsHost host, DataFileRow row)
    {
        Title = row.IsMirror ? "Mailbox copy settings" : "Data file settings";
        Width = 660; SizeToContent = SizeToContent.Height; MinWidth = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Themed.Brush("OlCard", Brushes.White);
        var gray = Themed.Brush("OlTextSecondary", Brushes.Gray);
        var stack = new StackPanel { Spacing = 10, Margin = new Thickness(22, 18) };
        var icon = Themed.Icon(row.IsMirror ? "OlIconMail" : "OlIconOpenArchive", 28);
        stack.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            Children = { icon, new StackPanel { Children = { new TextBlock { Text = row.Name, FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Themed.Brush("OlText", Brushes.Black) },
                new TextBlock { Text = row.Kind, FontSize = 12, Foreground = gray } } } }
        });
        stack.Children.Add(new TextBlock { Text = row.Available ? row.Path : "Mailbox copies for this account type are not available yet.", TextWrapping = TextWrapping.Wrap, Foreground = gray, FontSize = 13 });
        stack.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            Children = { new TextBlock { Text = AccountSettingsWindow.FormatSize(row.SizeBytes), FontSize = 13 }, new TextBlock { Text = "·", Foreground = gray },
                new TextBlock { Text = row.Status, FontSize = 13, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = row.LastSync is { } l ? $"· last synchronised {l.ToLocalTime():g}" : "", Foreground = gray, FontSize = 13 } }
        });
        if (row.IsMirror && row.AccountId is { } id && row.Settings is { } s)
        {
            stack.Children.Add(new Border { Height = 1, Background = Themed.Brush("OlHairline", Brushes.LightGray), Margin = new Thickness(0, 4) });
            var enabled = new CheckBox { Content = "Keep a copy of this mailbox on this computer", IsChecked = s.Enabled };
            var months = new ComboBox { Width = 160 };
            var choices = new (string Label, int Months)[] { ("All mail", 0), ("12 months", 12), ("6 months", 6), ("3 months", 3), ("1 month", 1) };
            foreach (var c in choices) months.Items.Add(new ComboBoxItem { Content = c.Label, Tag = c.Months });
            months.SelectedIndex = Math.Max(0, Array.FindIndex(choices, c => c.Months == s.KeepMonths));
            var cap = new ComboBox { Width = 130 };
            var caps = new (string Label, long Bytes)[] { ("5 MB", 5L << 20), ("25 MB", 25L << 20), ("100 MB", 100L << 20) };
            foreach (var c in caps) cap.Items.Add(new ComboBoxItem { Content = c.Label, Tag = c.Bytes });
            cap.SelectedIndex = Math.Max(0, Array.FindIndex(caps, c => c.Bytes == s.MaxAttachmentBytes));
            void Save()
            {
                var m = (months.SelectedItem as ComboBoxItem)?.Tag is int mm ? mm : 12;
                var b = (cap.SelectedItem as ComboBoxItem)?.Tag is long bb ? bb : 25L << 20;
                host.SaveMirrorSettings(id, s with { Enabled = enabled.IsChecked == true, KeepMonths = m, MaxAttachmentBytes = b });
            }
            enabled.IsCheckedChanged += (_, _) => Save();
            months.SelectionChanged += (_, _) => Save();
            cap.SelectionChanged += (_, _) => Save();
            stack.Children.Add(enabled);
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,24,Auto,Auto"), RowDefinitions = new RowDefinitions("Auto") };
            void At(Control c, int col) { Grid.SetColumn(c, col); grid.Children.Add(c); }
            At(new TextBlock { Text = "Keep mail from the last", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), FontSize = 13 }, 0);
            At(months, 1);
            At(new TextBlock { Text = "Largest attachment", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), FontSize = 13 }, 3);
            At(cap, 4);
            stack.Children.Add(grid);
            var sync = Tool("Sync now", "OlIconRefresh");
            sync.Click += async (_, _) => { sync.IsEnabled = false; try { await host.SyncNowAsync(id); Note = "Synchronised."; } finally { sync.IsEnabled = true; } };
            var relocate = Tool("Change location…", "OlIconOpenExport");
            relocate.Click += async (_, _) => { var n = await host.ChangeLocationAsync(id, this); if (n is not null) Note = n; Close(); };
            stack.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 4, 0, 0), Children = { sync, relocate } });
        }
        var ok = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(26, 6), Margin = new Thickness(0, 8, 0, 0), IsDefault = true };
        ok.Click += (_, _) => Close();
        stack.Children.Add(ok);
        Content = stack;
    }

    private static Button Tool(string caption, string icon)
    {
        var b = new Button { Classes = { "olSmall" }, Padding = new Thickness(9, 5) };
        b.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children = { Themed.Icon(icon, 16), new TextBlock { Text = caption, FontSize = 13, VerticalAlignment = VerticalAlignment.Center } } };
        return b;
    }
}
