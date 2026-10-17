using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using OpenOutlook.Auth;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Desktop;

/// <summary>
/// The People section (Outlook's Contacts view) for a personal Microsoft account: "My Contacts" folders, the A-Z jump index, the contact
/// list with round avatars, a details card, and the Home ribbon. Contacts are read and written through Microsoft Graph (Contacts.ReadWrite).
/// </summary>
public sealed class PeoplePage : UserControl
{
    public const string PlannedFeature = "Planned feature";

    private readonly Window _owner;
    private readonly HttpClient _http;
    private readonly Func<ConnectedAccount, Task<string>> _token;
    private readonly Func<ConnectedAccount?, Task> _accountSetup;
    private readonly Action<ConnectedAccount, string, string> _email;
    private readonly Func<ConnectedAccount, string, Task> _meeting;

    private readonly ComboBox _accounts = new() { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(4, 0, 4, 6) };
    private readonly ListBox _folders = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
    private readonly ListBox _list = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
    private readonly StackPanel _index = new() { Spacing = 0, Margin = new Thickness(2, 6, 2, 6) };
    private readonly StackPanel _details = new() { Spacing = 6, Margin = new Thickness(18, 16) };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 6), Foreground = Brushes.Gray, IsVisible = false };
    private readonly TextBlock _count = new() { Margin = new Thickness(10, 4), FontSize = 12, Foreground = Brushes.Gray };
    private readonly Button _connect = new() { Content = "Connect or repair account", IsVisible = false, Margin = new Thickness(6, 8) };
    private readonly TextBox _search = new() { Watermark = "Search People", Width = 170, MinHeight = 26 };
    private readonly List<(bool List, Button Button)> _viewButtons = [];

    private IReadOnlyList<GraphContact> _contacts = [];
    private readonly Dictionary<string, byte[]?> _photos = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Border> _avatars = new(StringComparer.Ordinal);
    private CancellationTokenSource? _load;
    private long _version;
    private bool _active;
    private bool _listView;
    private bool _settingFolders;

    public Control Ribbon { get; }
    /// <summary>The page's own status line (also handed to the main window's status bar by the caller when wanted).</summary>
    public event Action<string>? Status;
    /// <summary>A contact of the account (its id) was created, changed or deleted here: the compose window's suggestions read the contacts again.</summary>
    public event Action<string>? ContactsChanged;

    public PeoplePage(Window owner, HttpClient http, Func<ConnectedAccount, Task<string>> token, Func<ConnectedAccount?, Task> accountSetup,
        Action? openFile, Action<ConnectedAccount, string, string> email, Func<ConnectedAccount, string, Task> meeting)
    {
        _owner = owner;
        _http = http;
        _token = token;
        _accountSetup = accountSetup;
        _email = email;
        _meeting = meeting;
        Ribbon = BuildRibbon(openFile);
        Content = BuildLayout();

        _accounts.SelectionChanged += async (_, _) => { if (_active) await LoadFoldersAsync(); };
        _folders.SelectionChanged += async (_, _) => { if (_active && !_settingFolders) await LoadContactsAsync(); };
        _list.SelectionChanged += (_, _) => ShowDetails();
        _list.DoubleTapped += async (_, _) => await EditSelectedAsync();
        _list.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Delete) { e.Handled = true; await DeleteSelectedAsync(); }
            else if (e.Key == Key.Enter) { e.Handled = true; await EditSelectedAsync(); }
        };
        _search.TextChanged += (_, _) => RenderList();
        _connect.Click += async (_, _) => { await _accountSetup(SelectedAccount); if (_active) await LoadFoldersAsync(); };
        BuildIndex();
        ShowDetails();
    }

    // ---------------------------------------------------------------- public surface

    public void SetAccounts(IReadOnlyList<ConnectedAccount> accounts)
    {
        var selected = SelectedAccount?.AccountId;
        _accounts.Items.Clear();
        foreach (var a in accounts.Where(a => a.Provider == OAuthProvider.MicrosoftConsumers))
            _accounts.Items.Add(new ComboBoxItem { Content = a.DisplayAddress, Tag = a });
        _accounts.SelectedItem = _accounts.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (i.Tag as ConnectedAccount)?.AccountId == selected)
            ?? _accounts.Items.OfType<ComboBoxItem>().FirstOrDefault();
        _accounts.IsVisible = _accounts.Items.Count > 1;
    }

    public async Task ActivateAsync()
    {
        _active = true;
        if (_contacts.Count == 0) await LoadFoldersAsync();
    }

    public void Deactivate()
    {
        _active = false;
        _load?.Cancel();
    }

    public string? SelectedAccountId => SelectedAccount?.AccountId;
    internal IReadOnlyList<GraphContact> Contacts => _contacts;

    private ConnectedAccount? SelectedAccount => (_accounts.SelectedItem as ComboBoxItem)?.Tag as ConnectedAccount;
    private GraphContactFolder? SelectedFolder => (_folders.SelectedItem as ListBoxItem)?.Tag as GraphContactFolder;
    private GraphContact? SelectedContact => (_list.SelectedItem as ListBoxItem)?.Tag as GraphContact;

    // ---------------------------------------------------------------- loading

    private (long Version, CancellationToken Token) StartLoad()
    {
        _load?.Cancel();
        _load = new CancellationTokenSource();
        return (++_version, _load.Token);
    }

    private void Say(string text, bool show = true)
    {
        _message.Text = text;
        _message.IsVisible = show && text.Length > 0;
        Status?.Invoke(text);
    }

    private async Task LoadFoldersAsync()
    {
        var (version, ct) = StartLoad();
        _contacts = [];
        _settingFolders = true;
        _folders.Items.Clear();
        _settingFolders = false;
        RenderList();
        var account = SelectedAccount;
        if (account is null)
        {
            Say("Connect an Outlook.com or Hotmail account to see its contacts.");
            _connect.Content = "Add Microsoft account";
            _connect.IsVisible = true;
            return;
        }
        if (!account.CanManageMicrosoftContacts)
        {
            Say($"Contact access is not saved for {account.DisplayAddress}. Repair this account's sign-in (File > Info > Account Settings > Repair) to use People.");
            _connect.Content = "Repair selected account sign-in";
            _connect.IsVisible = true;
            return;
        }
        _connect.IsVisible = false;
        Say("Loading contact folders…");
        try
        {
            var token = await _token(account);
            var folders = await new GraphContactsClient(_http, account.AccountId).ListFoldersAsync(token, ct);
            if (ct.IsCancellationRequested || version != _version) return;
            _settingFolders = true;
            foreach (var folder in folders)
                _folders.Items.Add(new ListBoxItem { Content = new TextBlock { Text = folder.Name, FontWeight = folder.IsDefault ? FontWeight.SemiBold : FontWeight.Normal }, Tag = folder, Padding = new Thickness(18, 5) });
            _folders.SelectedIndex = 0;
            _settingFolders = false;
            await LoadContactsAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is GraphMailException or HttpRequestException or InvalidOperationException)
        {
            if (version == _version) Say(Describe(ex));
        }
    }

    private async Task LoadContactsAsync(string? select = null)
    {
        if (SelectedAccount is not { } account || SelectedFolder is not { } folder) return;
        var (version, ct) = StartLoad();
        Say("Loading contacts…");
        try
        {
            var token = await _token(account);
            var contacts = await new GraphContactsClient(_http, account.AccountId).ListContactsAsync(token, folder, ct);
            if (ct.IsCancellationRequested || version != _version) return;
            _contacts = contacts.OrderBy(ContactFormat.SortKey, StringComparer.CurrentCultureIgnoreCase).ToArray();
            Say("", false);
            RenderList(select);
            _ = LoadPhotosAsync(account, token, version, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is GraphMailException or HttpRequestException or InvalidOperationException)
        {
            if (version == _version) Say(Describe(ex));
        }
    }

    /// <summary>Pictures arrive in the background, four at a time, for the first 300 contacts of the folder.</summary>
    private async Task LoadPhotosAsync(ConnectedAccount account, string token, long version, CancellationToken ct)
    {
        var client = new GraphContactsClient(_http, account.AccountId);
        var pending = _contacts.Where(c => !_photos.ContainsKey(c.Id)).Take(300).ToList();
        for (var i = 0; i < pending.Count && !ct.IsCancellationRequested && version == _version; i += 4)
        {
            var batch = pending.Skip(i).Take(4).ToArray();
            var results = await Task.WhenAll(batch.Select(async c =>
            {
                try { return await client.GetPhotoAsync(token, c.Id, ct); }
                catch (Exception ex) when (ex is GraphMailException or HttpRequestException or OperationCanceledException) { return null; }
            }));
            if (ct.IsCancellationRequested || version != _version) return;
            for (var k = 0; k < batch.Length; k++)
            {
                _photos[batch[k].Id] = results[k] is { } bytes && IsDisplayable(bytes) ? bytes : null;
                if (_photos[batch[k].Id] is not null && _avatars.TryGetValue(batch[k].Id, out var avatar))
                    FillAvatar(avatar, batch[k], avatar.Width);
            }
            if (SelectedContact is { } shown && batch.Any(c => c.Id == shown.Id)) ShowDetails();
        }
    }

    private static bool IsDisplayable(byte[] bytes)
    {
        try { SafeInlineImage.Validate(bytes); return true; }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException) { return false; }
    }

    private static string Describe(Exception ex) => ex is GraphMailException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized }
        ? "Microsoft refused access to the contacts. Repair this account's sign-in (File > Info > Account Settings > Repair) to grant contact access."
        : "The contacts could not be loaded: " + ex.Message;

    // ---------------------------------------------------------------- list

    private IEnumerable<GraphContact> Filtered()
    {
        var q = (_search.Text ?? "").Trim();
        if (q.Length == 0) return _contacts;
        return _contacts.Where(c => new[] { ContactFormat.ShownName(c), c.CompanyName, c.JobTitle, c.FileAs, c.MobilePhone }
            .Concat(c.Emails.Select(e => e.Address)).Concat(c.BusinessPhones).Concat(c.HomePhones)
            .Any(text => text.Contains(q, StringComparison.CurrentCultureIgnoreCase)));
    }

    private void RenderList(string? select = null)
    {
        var keep = select ?? SelectedContact?.Id;
        _avatars.Clear();
        _list.Items.Clear();
        var shown = Filtered().ToList();
        foreach (var c in shown) _list.Items.Add(new ListBoxItem { Tag = c, Content = _listView ? ListRow(c) : PeopleRow(c), Padding = new Thickness(0) });
        _list.SelectedItem = _list.Items.OfType<ListBoxItem>().FirstOrDefault(i => (i.Tag as GraphContact)?.Id == keep)
            ?? _list.Items.OfType<ListBoxItem>().FirstOrDefault();
        _count.Text = shown.Count == _contacts.Count ? $"Items: {shown.Count}" : $"Items: {shown.Count} of {_contacts.Count}";
        if (_contacts.Count == 0 && SelectedFolder is not null && !_message.IsVisible) Say("There are no contacts in this folder.");
        ShowDetails();
    }

    private Control PeopleRow(GraphContact c)
    {
        var avatar = Avatar(c, 44);
        _avatars[c.Id] = avatar;
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        names.Children.Add(new TextBlock { Text = ContactFormat.ShownName(c), FontSize = 16, TextTrimming = TextTrimming.CharacterEllipsis });
        var sub = ContactFormat.SubLine(c);
        if (sub.Length > 0) names.Children.Add(new TextBlock { Text = sub, FontSize = 12, Foreground = Brushes.DimGray, TextTrimming = TextTrimming.CharacterEllipsis });
        return new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 7), Children = { avatar, names } };
    }

    private Control ListRow(GraphContact c)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,1.4*,2*,1.3*"), Margin = new Thickness(10, 5) };
        string[] cells = [ContactFormat.ShownName(c), c.CompanyName, c.Emails.FirstOrDefault()?.Address ?? "",
            c.BusinessPhones.Concat(c.HomePhones).Append(c.MobilePhone).FirstOrDefault(p => p.Length > 0) ?? ""];
        for (var i = 0; i < cells.Length; i++)
        {
            var t = new TextBlock { Text = cells[i], TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 10, 0), FontSize = 13 };
            Grid.SetColumn(t, i);
            grid.Children.Add(t);
        }
        return grid;
    }

    private Border Avatar(GraphContact c, double size)
    {
        var border = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center };
        FillAvatar(border, c, size);
        return border;
    }

    private void FillAvatar(Border border, GraphContact c, double size)
    {
        if (_photos.TryGetValue(c.Id, out var bytes) && bytes is not null)
        {
            try
            {
                border.Background = null;
                border.Child = new Image { Source = new Bitmap(new MemoryStream(bytes)), Stretch = Stretch.UniformToFill, Width = size, Height = size };
                return;
            }
            catch (Exception) { /* a picture Avalonia cannot decode: initials instead */ }
        }
        var name = ContactFormat.ShownName(c);
        border.Background = new SolidColorBrush(ContactFormat.AvatarColor(name));
        border.Child = new TextBlock
        {
            Text = ContactFormat.Initials(name), Foreground = Brushes.White, FontSize = size * 0.38,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
    }

    private void BuildIndex()
    {
        foreach (var key in new[] { "123" }.Concat(Enumerable.Range('a', 26).Select(c => ((char)c).ToString())))
        {
            var b = new Button
            {
                Content = key, FontSize = 11, Padding = new Thickness(4, 1), MinHeight = 0, MinWidth = 26, Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, Foreground = Brushes.DimGray
            };
            ToolTip.SetTip(b, key == "123" ? "Jump to names that start with a number or symbol" : $"Jump to {key.ToUpperInvariant()}");
            b.Click += (_, _) => JumpTo(key);
            _index.Children.Add(b);
        }
    }

    /// <summary>Selects the first contact filed under the letter, or the next letter that has one (like Outlook's index).</summary>
    internal void JumpTo(string key)
    {
        var items = _list.Items.OfType<ListBoxItem>().ToList();
        static int Rank(string k) => k == "123" ? 0 : k[0] - 'a' + 1;
        var target = items.FirstOrDefault(i => i.Tag is GraphContact c && Rank(ContactFormat.IndexKey(ContactFormat.SortKey(c))) >= Rank(key));
        if (target is null) return;
        _list.SelectedItem = target;
        _list.ScrollIntoView(target);
    }

    // ---------------------------------------------------------------- details card

    private void ShowDetails()
    {
        _details.Children.Clear();
        if (SelectedContact is not { } c)
        {
            _details.Children.Add(new TextBlock { Text = _contacts.Count == 0 ? "" : "Select a contact to see the details.", Foreground = Brushes.Gray });
            return;
        }
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        header.Children.Add(Avatar(c, 88));
        var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        title.Children.Add(new SelectableTextBlock { Text = ContactFormat.ShownName(c), FontSize = 24, TextWrapping = TextWrapping.Wrap });
        var role = string.Join(", ", new[] { c.JobTitle, c.CompanyName }.Where(p => p.Length > 0));
        if (role.Length > 0) title.Children.Add(new TextBlock { Text = role, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap });
        header.Children.Add(title);
        _details.Children.Add(header);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(104, 4, 0, 6) };
        var email = c.Emails.FirstOrDefault();
        actions.Children.Add(Action("OlIconMail", email is null ? "No email address" : "Send email", email is null ? null : () => { SendEmail(c); return Task.CompletedTask; }));
        actions.Children.Add(Action("OlIconCalendar", email is null ? "No email address" : "Schedule a meeting", email is null ? null : () => MeetingAsync(c)));
        actions.Children.Add(Action("OlIconEditDraft", "Edit contact", EditSelectedAsync));
        _details.Children.Add(actions);
        _details.Children.Add(new Border { Height = 1, Background = Brushes.Gainsboro, Margin = new Thickness(0, 4) });
        _details.Children.Add(new TextBlock { Text = "Contact", FontSize = 17, Margin = new Thickness(0, 4, 0, 4) });

        void Row(string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*"), Margin = new Thickness(0, 2) };
            row.Children.Add(new TextBlock { Text = label, Foreground = Brushes.Gray, FontSize = 13 });
            var v = new SelectableTextBlock { Text = value.Trim(), TextWrapping = TextWrapping.Wrap, FontSize = 13 };
            Grid.SetColumn(v, 1);
            row.Children.Add(v);
            _details.Children.Add(row);
        }
        for (var i = 0; i < c.Emails.Count; i++) Row(i == 0 ? "Email" : $"Email {i + 1}", c.Emails[i].Address);
        for (var i = 0; i < c.BusinessPhones.Count; i++) Row(i == 0 ? "Work phone" : "Work phone 2", c.BusinessPhones[i]);
        for (var i = 0; i < c.HomePhones.Count; i++) Row(i == 0 ? "Home phone" : "Home phone 2", c.HomePhones[i]);
        Row("Mobile", c.MobilePhone);
        Row("Work address", ContactFormat.FormatAddress(c.BusinessAddress));
        Row("Home address", ContactFormat.FormatAddress(c.HomeAddress));
        Row("Other address", ContactFormat.FormatAddress(c.OtherAddress));
        Row("Web page", c.BusinessHomePage);
        Row("IM", string.Join("\n", c.ImAddresses));
        Row("Birthday", c.Birthday?.ToString("MMMM d, yyyy") ?? "");
        Row("Notes", c.PersonalNotes);
        if (c.Emails.Count == 0 && c.BusinessPhones.Count + c.HomePhones.Count == 0 && c.MobilePhone.Length == 0)
            _details.Children.Add(new TextBlock { Text = "There is no email address or phone number for this person.", Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
    }

    private Button Action(string icon, string tip, Func<Task>? action)
    {
        var b = new Button { Content = Icon(icon, 18), Padding = new Thickness(7), Background = Brushes.Transparent, BorderThickness = new Thickness(1), BorderBrush = Brushes.Gainsboro, IsEnabled = action is not null };
        ToolTip.SetTip(b, tip);
        ToolTip.SetShowOnDisabled(b, true);
        if (action is not null) b.Click += async (_, _) => await action();
        return b;
    }

    // ---------------------------------------------------------------- commands

    private void SendEmail(GraphContact c)
    {
        if (SelectedAccount is not { } account || c.Emails.FirstOrDefault() is not { } first) return;
        _email(account, ContactFormat.ShownName(c), first.Address);
    }

    private async Task MeetingAsync(GraphContact c)
    {
        if (SelectedAccount is not { } account || c.Emails.FirstOrDefault() is not { } first) return;
        await _meeting(account, first.Address);
    }

    private async Task NewContactAsync()
    {
        if (SelectedAccount is not { } account || SelectedFolder is not { } folder) { Say("Open a contact folder first."); return; }
        var window = new ContactEditorWindow(null, null, (contact, picture) => SaveAsync(account, folder, contact, picture),
            email: (name, address) => _email(account, name, address), meeting: address => _meeting(account, address));
        await window.ShowDialog(_owner);
    }

    private async Task EditSelectedAsync()
    {
        if (SelectedAccount is not { } account || SelectedFolder is not { } folder || SelectedContact is not { } contact) return;
        _photos.TryGetValue(contact.Id, out var photo);
        var window = new ContactEditorWindow(contact, photo, (c, picture) => SaveAsync(account, folder, c, picture),
            delete: c => DeleteAsync(account, c), email: (name, address) => _email(account, name, address), meeting: address => _meeting(account, address));
        await window.ShowDialog(_owner);
    }

    private async Task DeleteSelectedAsync()
    {
        if (SelectedAccount is not { } account || SelectedContact is not { } contact) return;
        if (!await ConfirmAsync($"Delete {ContactFormat.ShownName(contact)}?", "The contact is removed from your Microsoft account (it goes to Deleted Items there).")) return;
        var error = await DeleteAsync(account, contact);
        if (error is not null) Say(error);
    }

    /// <summary>Creates or updates through Graph, uploads a new picture, and puts the result in the list.</summary>
    private async Task<string?> SaveAsync(ConnectedAccount account, GraphContactFolder folder, GraphContact contact, byte[]? picture)
    {
        try
        {
            var token = await _token(account);
            var client = new GraphContactsClient(_http, account.AccountId);
            var saved = contact.Id.Length == 0 ? await client.CreateContactAsync(token, folder, contact) : await client.UpdateContactAsync(token, contact);
            string? note = null;
            if (picture is not null)
            {
                try { await client.SetPhotoAsync(token, saved.Id, picture); _photos[saved.Id] = picture; }
                catch (Exception ex) when (ex is GraphMailException or HttpRequestException or ArgumentException) { note = " The picture could not be saved: " + ex.Message; }
            }
            _contacts = _contacts.Where(c => c.Id != saved.Id).Append(saved).OrderBy(ContactFormat.SortKey, StringComparer.CurrentCultureIgnoreCase).ToArray();
            RenderList(saved.Id);
            ContactsChanged?.Invoke(account.AccountId);
            Say((contact.Id.Length == 0 ? "Contact saved." : "Contact updated.") + (note ?? ""), note is not null);
            return null;
        }
        catch (Exception ex) when (ex is GraphMailException or HttpRequestException or ArgumentException or InvalidOperationException)
        {
            return "The contact could not be saved: " + ex.Message;
        }
    }

    private async Task<string?> DeleteAsync(ConnectedAccount account, GraphContact contact)
    {
        try
        {
            var token = await _token(account);
            await new GraphContactsClient(_http, account.AccountId).DeleteContactAsync(token, contact.Id);
            var index = _list.SelectedIndex;
            _contacts = _contacts.Where(c => c.Id != contact.Id).ToArray();
            _photos.Remove(contact.Id);
            ContactsChanged?.Invoke(account.AccountId);
            RenderList();
            if (_list.ItemCount > 0) _list.SelectedIndex = Math.Clamp(index, 0, _list.ItemCount - 1);
            Say("Contact deleted.", false);
            return null;
        }
        catch (Exception ex) when (ex is GraphMailException or HttpRequestException or ArgumentException or InvalidOperationException)
        {
            return "The contact could not be deleted: " + ex.Message;
        }
    }

    private async Task<bool> ConfirmAsync(string heading, string text)
    {
        var dialog = new Window { Title = "OpenOutlook", Width = 420, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        var yes = new Button { Content = "Delete", IsDefault = true, MinWidth = 80 };
        var no = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 10,
            Children =
            {
                new TextBlock { Text = heading, FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { yes, no } }
            }
        };
        return await dialog.ShowDialog<bool>(_owner);
    }

    private void SetView(bool list)
    {
        _listView = list;
        foreach (var (isList, button) in _viewButtons) button.Classes.Set("olCalViewSelected", isList == list);
        RenderList();
    }

    // ---------------------------------------------------------------- layout

    private Control BuildLayout()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("240,Auto,*,400") };
        var sidebar = new DockPanel { Margin = new Thickness(4, 8, 0, 0) };
        var title = new TextBlock { Text = "My Contacts", FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(8, 0, 0, 6) };
        DockPanel.SetDock(title, Dock.Top);
        sidebar.Children.Add(title);
        DockPanel.SetDock(_accounts, Dock.Top);
        sidebar.Children.Add(_accounts);
        DockPanel.SetDock(_connect, Dock.Bottom);
        sidebar.Children.Add(_connect);
        sidebar.Children.Add(_folders);
        grid.Children.Add(new Border { BorderBrush = Brushes.Gainsboro, BorderThickness = new Thickness(0, 0, 1, 0), Child = sidebar });

        var index = new ScrollViewer { Content = _index, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden };
        Grid.SetColumn(index, 1);
        grid.Children.Add(index);

        var middle = new DockPanel();
        DockPanel.SetDock(_message, Dock.Top);
        middle.Children.Add(_message);
        DockPanel.SetDock(_count, Dock.Bottom);
        middle.Children.Add(_count);
        middle.Children.Add(_list);
        Grid.SetColumn(middle, 2);
        grid.Children.Add(middle);

        var right = new Border { BorderBrush = Brushes.Gainsboro, BorderThickness = new Thickness(1, 0, 0, 0), Child = new ScrollViewer { Content = _details } };
        Grid.SetColumn(right, 3);
        grid.Children.Add(right);
        return grid;
    }

    private Control BuildRibbon(Action? openFile)
    {
        var tabs = new TabControl { Classes = { "olTabs" }, Padding = new Thickness(0) };
        tabs.Items.Add(new TabItem { Header = "File" });
        tabs.Items.Add(new TabItem { Header = "Home", Content = Scroll(HomeRibbon()) });
        tabs.Items.Add(new TabItem { Header = "Send / Receive", Content = Scroll(Strip(Group("Send & Receive", Planned("Send/Receive\nAll Folders", "OlIconSendReceive"), Planned("Update\nFolder", "OlIconRefresh")))) });
        tabs.Items.Add(new TabItem { Header = "Folder", Content = Scroll(Strip(Group("New", Planned("New\nFolder", "OlIconNewFolder")), Group("Actions", Planned("Rename\nFolder", "OlIconEditDraft"), Planned("Delete\nFolder", "OlIconDelete")), Group("Properties", Planned("Folder\nProperties", "OlIconGear")))) });
        tabs.Items.Add(new TabItem { Header = "View", Content = Scroll(Strip(Group("Current View", ViewButton("People", false), ViewButton("List", true), Planned("Change\nView", "OlIconReadingPane")), Group("Layout", Planned("Folder\nPane", "OlIconReadingPane"), Planned("Reading\nPane", "OlIconReadingPane")))) });
        tabs.Items.Add(new TabItem { Header = "Help", Content = Scroll(Strip(Group("Help", Planned("Help", "OlIconHelp")))) });
        tabs.SelectedIndex = 1;
        tabs.SelectionChanged += (_, _) =>
        {
            if (tabs.SelectedIndex != 0 || openFile is null) return;
            tabs.SelectedIndex = 1;
            openFile();
        };
        return tabs;
    }

    private StackPanel HomeRibbon()
    {
        var refresh = Command("Refresh", "OlIconRefresh", () => LoadContactsAsync());
        var search = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { _search, SmallPlanned("Address Book", "OlIconAddressBook") } };
        return Strip(
            Group("New", Command("New\nContact", "OlIconPeople", NewContactAsync), Planned("New Contact\nGroup", "OlIconPeople"), Planned("New\nItems", "OlIconNewItems")),
            Group("Delete", Command("Delete", "OlIconDelete", DeleteSelectedAsync)),
            Group("Communicate", Command("Email", "OlIconMail", () => { if (SelectedContact is { } c) SendEmail(c); return Task.CompletedTask; }),
                Command("Meeting", "OlIconCalendar", () => SelectedContact is { } c ? MeetingAsync(c) : Task.CompletedTask), Planned("More", "OlIconMore")),
            Group("Current View", ViewButton("People", false), Planned("Business\nCard", "OlIconPeopleR"), Planned("Card", "OlIconPeopleR"), Planned("Phone", "OlIconPeopleR"), ViewButton("List", true)),
            Group("Actions", Planned("Move", "OlIconMove"), Planned("Mail\nMerge", "OlIconMail")),
            Group("Share", Planned("Forward\nContact", "OlIconForward"), Planned("Share\nContacts", "OlIconPeople"), Planned("Open Shared\nContacts", "OlIconPeople")),
            Group("Tags", Planned("Categorize", "OlIconCategorize"), Planned("Follow\nUp", "OlIconFlag"), Planned("Private", "OlIconLock")),
            Group("Groups", new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { SmallPlanned("New Group", "OlIconPeople"), SmallPlanned("Browse Groups", "OlIconPeople") } }),
            Group("Find", search),
            Group("Update", refresh));
    }

    private static StackPanel Strip(params Control[] groups)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 3) };
        foreach (var g in groups) s.Children.Add(g);
        return s;
    }

    private static Control Scroll(Control content) => new ScrollViewer
    {
        Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
    };

    private static Border Group(string caption, params Control[] commands)
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
        if (Application.Current is { } app && app.TryFindResource(key, out var template) && template is IControlTemplate controlTemplate)
            return new ContentControl { Template = controlTemplate, Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center };
        return new TextBlock { Text = "▦", FontSize = size, HorizontalAlignment = HorizontalAlignment.Center };
    }

    private static Button Large(string caption, string icon)
    {
        var button = new Button { Classes = { "olLarge" } };
        button.Content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Children = { Icon(icon, 28), new TextBlock { Text = caption, Classes = { "olCaption" } } } };
        return button;
    }

    private Button Command(string caption, string icon, Func<Task> action)
    {
        var button = Large(caption, icon);
        ToolTip.SetTip(button, caption.Replace('\n', ' '));
        button.Click += async (_, _) => await action();
        return button;
    }

    /// <summary>A ribbon button whose feature is not built yet: it looks like the others, says "Planned feature" on hover and in the status line when clicked.</summary>
    private Button Planned(string caption, string icon)
    {
        var button = Large(caption, icon);
        ToolTip.SetTip(button, PlannedFeature);
        button.Click += (_, _) => Status?.Invoke(caption.Replace('\n', ' ') + ": " + PlannedFeature.ToLowerInvariant() + ".");
        return button;
    }

    private Button SmallPlanned(string caption, string icon)
    {
        var button = new Button
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(4, 2),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { Icon(icon, 16), new TextBlock { Text = caption, VerticalAlignment = VerticalAlignment.Center } } }
        };
        ToolTip.SetTip(button, PlannedFeature);
        button.Click += (_, _) => Status?.Invoke(caption + ": " + PlannedFeature.ToLowerInvariant() + ".");
        return button;
    }

    private Button ViewButton(string caption, bool list)
    {
        var button = Large(caption, list ? "OlIconReadingPane" : "OlIconPeople");
        ToolTip.SetTip(button, list ? "List view: name, company, email and phone in columns" : "People view: names with pictures");
        button.Click += (_, _) => SetView(list);
        button.Classes.Set("olCalViewSelected", list == _listView);
        _viewButtons.Add((list, button));
        return button;
    }
}
