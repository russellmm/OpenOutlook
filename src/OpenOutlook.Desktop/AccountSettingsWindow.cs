using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OpenOutlook.Auth;
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
    Task<string?> RemoveDataFileAsync(DataFileRow row);
    void OpenFileLocation(DataFileRow row);
    Task SyncNowAsync(string accountId);
    Task<string?> ChangeLocationAsync(string accountId, Window owner);
    void SaveMirrorSettings(string accountId, MirrorAccountSettings settings);
}

/// <summary>Account Settings, laid out like Outlook's dialog: an Email tab (the connected accounts) and a Data Files tab (mailbox copies and opened PST files).</summary>
public sealed class AccountSettingsWindow : Window
{
    private readonly IAccountSettingsHost _host;
    private readonly ListBox _emailList = new() { SelectionMode = SelectionMode.Single };
    private readonly ListBox _fileList = new() { SelectionMode = SelectionMode.Single };
    private readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), MinHeight = 32 };
    private readonly TabControl _tabs = new();
    private IReadOnlyList<ConnectedAccount> _accounts = [];
    private IReadOnlyList<DataFileRow> _files = [];

    public AccountSettingsWindow(IAccountSettingsHost host, bool startOnDataFiles = false)
    {
        _host = host;
        Title = "Account Settings";
        Width = 920; Height = 640; MinWidth = 720; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var heading = new TextBlock { Text = "Account Settings", FontSize = 20, FontWeight = FontWeight.SemiBold };
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(26, 6) };
        close.Click += (_, _) => Close();

        _tabs.Items.Add(new TabItem { Header = "Email", Content = BuildEmailTab() });
        _tabs.Items.Add(new TabItem { Header = "Data Files", Content = BuildDataFilesTab() });
        _tabs.SelectedIndex = startOnDataFiles ? 1 : 0;

        var bottom = new StackPanel { Children = { _note, close } };
        var dock = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(heading, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
        heading.Margin = new Thickness(0, 0, 0, 10);
        dock.Children.Add(heading); dock.Children.Add(bottom); dock.Children.Add(_tabs);
        Content = dock;
        Refresh();
    }

    // ---- toolbar helpers --------------------------------------------------------------------------------------------------------------------------

    private static Button Tool(string caption, string? tip = null)
    {
        var b = new Button { Content = caption, Padding = new Thickness(10, 5), Background = Brushes.Transparent };
        if (tip is not null) ToolTip.SetTip(b, tip);
        return b;
    }

    private static Button NotYet(string caption)
    {
        var b = Tool(caption, "To be implemented");
        return b;
    }

    private static Grid Row(params (Control Cell, GridLength Width)[] cells)
    {
        var grid = new Grid();
        for (var i = 0; i < cells.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(cells[i].Width));
            Grid.SetColumn(cells[i].Cell, i);
            grid.Children.Add(cells[i].Cell);
        }
        return grid;
    }

    private static TextBlock Cell(string text, bool bold = false, bool gray = false)
    {
        var t = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, Margin = new Thickness(6, 3) };
        if (gray) t.Foreground = Brushes.Gray;                  // never assign a null brush: it hides the text
        ToolTip.SetTip(t, text);
        return t;
    }

    private static Control Header(params (string Text, GridLength Width)[] cols) =>
        new Border
        {
            BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 6, 0, 0),
            Child = Row(cols.Select(c => ((Control)new TextBlock { Text = c.Text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(6, 3) }, c.Width)).ToArray())
        };

    // ---- Email tab --------------------------------------------------------------------------------------------------------------------------------

    private static readonly (string Text, GridLength Width)[] EmailColumns = [("Name", new GridLength(3, GridUnitType.Star)), ("Type", new GridLength(3, GridUnitType.Star))];

    private Control BuildEmailTab()
    {
        var add = Tool("New…", "Connect another Microsoft or Gmail account");
        var repair = Tool("Repair…", "Sign in again for the selected account");
        var change = NotYet("Change…");
        var makeDefault = Tool("Set as Default", "New messages are sent from the default account unless you choose another");
        var remove = Tool("Remove", "Disconnect the selected account from this computer");
        var up = NotYet("▲"); var down = NotYet("▼");
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
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { add, repair, change, makeDefault, remove, up, down } };
        var hint = new TextBlock { Text = "You can select an account and change its settings or remove it.", Margin = new Thickness(0, 8, 0, 2), Foreground = Brushes.Gray };
        var dock = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(tools, Dock.Top); DockPanel.SetDock(hint, Dock.Top);
        var header = Header(EmailColumns);
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(tools); dock.Children.Add(hint); dock.Children.Add(header); dock.Children.Add(_emailList);
        return dock;
    }

    private ConnectedAccount? SelectedAccount() => (_emailList.SelectedItem as ListBoxItem)?.Tag as ConnectedAccount;

    // ---- Data Files tab ---------------------------------------------------------------------------------------------------------------------------

    private static readonly (string Text, GridLength Width)[] FileColumns =
        [("Name", new GridLength(2, GridUnitType.Star)), ("Location", new GridLength(4, GridUnitType.Star)), ("Type", new GridLength(1.4, GridUnitType.Star)), ("Size", new GridLength(1, GridUnitType.Star))];

    private Control BuildDataFilesTab()
    {
        var add = Tool("Add…", "Open an Outlook data file (.pst) and show it in the folder list");
        var settings = Tool("Settings…", "How much of the mailbox the copy keeps, sync, and where the file is");
        var makeDefault = NotYet("Set as Default");
        var remove = Tool("Remove", "Close the file in OpenOutlook (the file itself is never deleted)");
        var open = Tool("Open File Location…", "Show the folder that contains the data file");
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
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { add, settings, makeDefault, remove, open } };
        var hint = new TextBlock
        {
            Text = "Select a data file in the list, then click Settings for more details or click Open File Location to display the folder that contains the data file.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 2), Foreground = Brushes.Gray
        };
        var dock = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(tools, Dock.Top); DockPanel.SetDock(hint, Dock.Bottom);
        var header = Header(FileColumns);
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(tools); dock.Children.Add(hint); dock.Children.Add(header); dock.Children.Add(_fileList);
        return dock;
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
        var selectedAccount = SelectedAccount()?.AccountId;
        var selectedFile = SelectedFile()?.Path;
        _accounts = _host.Accounts();
        _emailList.Items.Clear();
        var def = _host.DefaultAccountId ?? _accounts.FirstOrDefault()?.AccountId;
        foreach (var a in _accounts)
        {
            var isDefault = a.AccountId == def;
            var type = a.Provider == OAuthProvider.Google ? "Gmail (send and receive)" : "Microsoft mail (send and receive)";
            var item = new ListBoxItem { Tag = a, Content = Row((Cell((isDefault ? "✔  " : "     ") + a.DisplayAddress, bold: isDefault), EmailColumns[0].Width), (Cell(type), EmailColumns[1].Width)) };
            _emailList.Items.Add(item);
            if (a.AccountId == selectedAccount) _emailList.SelectedItem = item;
        }
        if (_accounts.Count == 0) _emailList.Items.Add(new ListBoxItem { Content = "No accounts are connected. Click New… to add one.", IsHitTestVisible = false });
        _files = _host.DataFiles();
        _fileList.Items.Clear();
        foreach (var f in _files)
        {
            var item = new ListBoxItem
            {
                Tag = f,
                Content = Row((Cell(f.Name, bold: f.IsMirror), FileColumns[0].Width), (Cell(f.Available ? f.Path : "Not available yet", gray: !f.Available), FileColumns[1].Width),
                    (Cell(f.Kind), FileColumns[2].Width), (Cell(f.SizeBytes > 0 ? FormatSize(f.SizeBytes) : ""), FileColumns[3].Width))
            };
            _fileList.Items.Add(item);
            if (f.Path == selectedFile) _fileList.SelectedItem = item;
        }
    }

    internal static string FormatSize(long bytes) =>
        bytes <= 0 ? "no file yet" : bytes < 1 << 20 ? $"{bytes / 1024.0:0.#} KB" : bytes < 1L << 30 ? $"{bytes / 1048576.0:0.#} MB" : $"{bytes / 1073741824.0:0.##} GB";
}

/// <summary>The Settings… dialog of one data file: for a mailbox copy its keep window, attachment limit, sync and location; for an opened file its details.</summary>
public sealed class DataFileSettingsDialog : Window
{
    public string? Note { get; private set; }

    public DataFileSettingsDialog(IAccountSettingsHost host, DataFileRow row)
    {
        Title = row.IsMirror ? "Mailbox copy settings" : "Data file settings";
        Width = 640; SizeToContent = SizeToContent.Height; MinWidth = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var stack = new StackPanel { Spacing = 8, Margin = new Thickness(18) };
        stack.Children.Add(new TextBlock { Text = row.Name, FontSize = 17, FontWeight = FontWeight.SemiBold });
        stack.Children.Add(new TextBlock { Text = row.Available ? row.Path : "Mailbox copies for this account type are not available yet.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray });
        stack.Children.Add(new TextBlock { Text = $"{row.Kind}   |   {AccountSettingsWindow.FormatSize(row.SizeBytes)}   |   {row.Status}" + (row.LastSync is { } l ? $"   |   last synchronised {l.ToLocalTime():g}" : ""), TextWrapping = TextWrapping.Wrap });
        if (row.IsMirror && row.AccountId is { } id && row.Settings is { } s)
        {
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
            stack.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { new TextBlock { Text = "Keep mail from the last", VerticalAlignment = VerticalAlignment.Center }, months,
                new TextBlock { Text = "Largest attachment", VerticalAlignment = VerticalAlignment.Center }, cap } });
            var sync = new Button { Content = "Sync now" };
            sync.Click += async (_, _) => { sync.IsEnabled = false; try { await host.SyncNowAsync(id); Note = "Synchronised."; } finally { sync.IsEnabled = true; } };
            var relocate = new Button { Content = "Change location…" };
            relocate.Click += async (_, _) => { var n = await host.ChangeLocationAsync(id, this); if (n is not null) Note = n; Close(); };
            stack.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { sync, relocate } });
        }
        var ok = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(22, 5) };
        ok.Click += (_, _) => Close();
        stack.Children.Add(ok);
        Content = stack;
    }
}
