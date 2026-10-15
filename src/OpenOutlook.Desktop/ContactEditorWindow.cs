using System.Net.Mail;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Desktop;

/// <summary>
/// The Outlook "Contact" window (General page): Full Name, Company, Job title, File as, three email slots with Display as, web page, IM, four phone
/// rows with a type picker, the address box with its type picker, picture, business-card preview and Notes. Save & Close / Save & New / Delete
/// go through the callbacks; every button whose feature is not built yet says "Planned feature".
/// </summary>
public sealed class ContactEditorWindow : Window
{
    public const string PlannedFeature = "Planned feature";

    /// <summary>Saves the contact (and the new picture when one was chosen); returns an error to show, or null when it was saved.</summary>
    public delegate Task<string?> SaveContact(GraphContact contact, byte[]? newPicture);

    private static readonly string[] PhoneTypes = ["Business", "Business 2", "Home", "Home 2", "Mobile"];
    private static readonly string[] UnsupportedPhoneTypes = ["Business Fax", "Home Fax", "Assistant", "Callback", "Car", "Company", "ISDN", "Other", "Other Fax", "Pager", "Primary", "Radio", "Telex", "TTY/TDD"];
    private static readonly string[] AddressTypes = ["Business", "Home", "Other"];

    private readonly SaveContact _save;
    private readonly Func<GraphContact, Task<string?>>? _delete;
    private readonly Action<string, string>? _email;
    private readonly Func<string, Task>? _meeting;
    private GraphContact _contact;
    private byte[]? _picture;
    private byte[]? _newPicture;

    private readonly TextBox _fullName = Field();
    private readonly TextBox _company = Field();
    private readonly TextBox _jobTitle = Field();
    private readonly ComboBox _fileAs = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 30 };
    private readonly ComboBox _emailSlot = new() { Width = 112 };
    private readonly TextBox _emailAddress = Field();
    private readonly TextBox _displayAs = Field();
    private readonly TextBox _webPage = Field();
    private readonly TextBox _im = Field();
    private readonly (ComboBox Type, TextBox Value)[] _phones = new (ComboBox, TextBox)[4];
    private readonly ComboBox _addressType = new() { Width = 112 };
    private readonly TextBox _address = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 110 };
    private readonly CheckBox _mailing = new() { Content = "This is the\nmailing address", IsEnabled = false };
    private readonly TextBox _notes = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, AcceptsTab = true };
    private readonly Button _pictureButton = new() { Width = 144, Height = 144, Padding = new Thickness(0), Background = Brushes.Transparent };
    private readonly StackPanel _card = new() { Spacing = 2, Margin = new Thickness(16, 12) };
    private readonly TextBlock _status = new() { Margin = new Thickness(12, 4), FontSize = 12, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap };

    private readonly string[] _emails = ["", "", ""];
    private readonly string[] _emailNames = ["", "", ""];
    private readonly Dictionary<string, string> _phoneValues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _addresses = new(StringComparer.Ordinal);
    private int _shownEmail;
    private int _shownAddress;
    private bool _loading;

    public bool Saved { get; private set; }

    public ContactEditorWindow(GraphContact? contact, byte[]? picture, SaveContact save, Func<GraphContact, Task<string?>>? delete = null,
        Action<string, string>? email = null, Func<string, Task>? meeting = null)
    {
        _contact = contact ?? new GraphContact();
        _picture = picture;
        _save = save;
        _delete = delete;
        _email = email;
        _meeting = meeting;
        Width = 1180; Height = 860; MinWidth = 900; MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = BuildLayout();
        Load(_contact);
        Opened += (_, _) => _fullName.Focus();
    }

    // ---------------------------------------------------------------- load / collect

    private void Load(GraphContact c)
    {
        _loading = true;
        _fullName.Text = ContactFormat.FullName(c);
        _company.Text = c.CompanyName;
        _jobTitle.Text = c.JobTitle;
        for (var i = 0; i < 3; i++)
        {
            _emails[i] = i < c.Emails.Count ? c.Emails[i].Address : "";
            _emailNames[i] = i < c.Emails.Count ? c.Emails[i].Name : "";
        }
        _shownEmail = 0;
        _emailSlot.SelectedIndex = 0;
        _emailAddress.Text = _emails[0];
        _displayAs.Text = _emailNames[0];
        _webPage.Text = c.BusinessHomePage;
        _im.Text = c.ImAddresses.FirstOrDefault() ?? "";
        _phoneValues.Clear();
        _phoneValues["Business"] = c.BusinessPhones.ElementAtOrDefault(0) ?? "";
        _phoneValues["Business 2"] = c.BusinessPhones.ElementAtOrDefault(1) ?? "";
        _phoneValues["Home"] = c.HomePhones.ElementAtOrDefault(0) ?? "";
        _phoneValues["Home 2"] = c.HomePhones.ElementAtOrDefault(1) ?? "";
        _phoneValues["Mobile"] = c.MobilePhone;
        string[] defaults = ["Business", "Home", "Business Fax", "Mobile"];
        for (var i = 0; i < 4; i++) { SelectType(_phones[i].Type, defaults[i]); ShowPhone(i); }
        _addresses["Business"] = ContactFormat.FormatAddress(c.BusinessAddress);
        _addresses["Home"] = ContactFormat.FormatAddress(c.HomeAddress);
        _addresses["Other"] = ContactFormat.FormatAddress(c.OtherAddress);
        _shownAddress = _addresses["Business"].Length == 0 && _addresses["Home"].Length > 0 ? 1 : 0;
        _addressType.SelectedIndex = _shownAddress;
        _address.Text = _addresses[AddressTypes[_shownAddress]];
        _notes.Text = c.PersonalNotes;
        _loading = false;
        RefreshFileAs(c.FileAs);
        RefreshPicture();
        RefreshCard();
        UpdateTitle();
    }

    /// <summary>The contact as the form shows it (fields the form does not show are kept from the loaded contact).</summary>
    internal GraphContact Collect()
    {
        StashEmail(); StashAddress();
        for (var i = 0; i < 4; i++) StashPhone(i);
        var name = ContactFormat.ParseFullName(_fullName.Text);
        var full = (_fullName.Text ?? "").Trim();
        var emails = new List<GraphEmailAddress>();
        for (var i = 0; i < 3; i++)
            if (_emails[i].Trim().Length > 0)
                emails.Add(new GraphEmailAddress(_emails[i].Trim(), _emailNames[i].Trim().Length > 0 ? _emailNames[i].Trim() : DefaultDisplayAs(_emails[i].Trim())));
        string[] Pair(string a, string b) => new[] { _phoneValues.GetValueOrDefault(a, ""), _phoneValues.GetValueOrDefault(b, "") }.Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
        var ims = new List<string>();
        if ((_im.Text ?? "").Trim().Length > 0) ims.Add(_im.Text!.Trim());
        ims.AddRange(_contact.ImAddresses.Skip(1));
        return _contact with
        {
            DisplayName = full.Length > 0 ? full : (_company.Text ?? "").Trim(),
            Title = name.Title, GivenName = name.Given, MiddleName = name.Middle, Surname = name.Surname, Suffix = name.Suffix,
            CompanyName = (_company.Text ?? "").Trim(),
            JobTitle = (_jobTitle.Text ?? "").Trim(),
            FileAs = _fileAs.SelectedItem as string ?? "",
            Emails = emails,
            BusinessPhones = Pair("Business", "Business 2"),
            HomePhones = Pair("Home", "Home 2"),
            MobilePhone = _phoneValues.GetValueOrDefault("Mobile", "").Trim(),
            BusinessAddress = ContactFormat.ParseAddress(_addresses.GetValueOrDefault("Business")),
            HomeAddress = ContactFormat.ParseAddress(_addresses.GetValueOrDefault("Home")),
            OtherAddress = ContactFormat.ParseAddress(_addresses.GetValueOrDefault("Other")),
            BusinessHomePage = (_webPage.Text ?? "").Trim(),
            ImAddresses = ims.Take(3).ToArray(),
            PersonalNotes = _notes.Text ?? ""
        };
    }

    private string DefaultDisplayAs(string address)
    {
        var full = (_fullName.Text ?? "").Trim();
        return full.Length > 0 ? $"{full} ({address})" : address;
    }

    /// <summary>Checks the form before it is saved; returns the problem or null.</summary>
    internal static string? Problem(GraphContact c)
    {
        if (string.IsNullOrWhiteSpace(c.DisplayName) && c.Emails.Count == 0)
            return "Enter a name, a company or an email address.";
        foreach (var e in c.Emails)
            if (!MailAddress.TryCreate(e.Address, out var parsed) || parsed.Address != e.Address)
                return $"\"{e.Address}\" is not a valid email address.";
        if (c.BusinessHomePage.Length > 0 && !Uri.TryCreate(c.BusinessHomePage.Contains("://") ? c.BusinessHomePage : "https://" + c.BusinessHomePage, UriKind.Absolute, out _))
            return "The web page address is not valid.";
        return null;
    }

    // ---------------------------------------------------------------- save / delete

    private async Task<bool> SaveAsync()
    {
        var contact = Collect();
        if (Problem(contact) is { } problem) { Fail(problem); return false; }
        Info("Saving…");
        var error = await _save(contact, _newPicture);
        if (error is not null) { Fail(error); return false; }
        Saved = true;
        return true;
    }

    private async Task SaveAndCloseAsync() { if (await SaveAsync()) Close(true); }

    private async Task SaveAndNewAsync()
    {
        if (!await SaveAsync()) return;
        _contact = new GraphContact();
        _picture = _newPicture = null;
        Load(_contact);
        Info("Saved. Enter the next contact.");
        _fullName.Focus();
    }

    private async Task DeleteAsync()
    {
        if (_contact.Id.Length == 0) { Close(false); return; }                  // a new contact is just discarded, as in Outlook
        if (_delete is null) { Info("Delete: " + PlannedFeature.ToLowerInvariant() + "."); return; }
        Info("Deleting…");
        var error = await _delete(_contact);
        if (error is not null) { Fail(error); return; }
        Saved = true;
        Close(true);
    }

    private async Task ChoosePictureAsync()
    {
        var files = await SafePick.FilesAsync(this, new FilePickerOpenOptions
        {
            Title = "Add Contact Picture", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Pictures") { Patterns = ["*.jpg", "*.jpeg", "*.png"] }]
        }, failure => Fail(failure));
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > GraphContactsClient.MaxPhotoBytes) { Fail("Choose a picture of 4 MB or less."); return; }
            var bytes = await File.ReadAllBytesAsync(path);
            var isJpeg = bytes.Length > 3 && bytes[0] == 0xff && bytes[1] == 0xd8;
            var isPng = bytes.Length > 8 && bytes[0] == 137 && bytes[1] == 80;
            if (!isJpeg && !isPng) { Fail("Choose a JPEG or PNG picture."); return; }
            SafeInlineImage.Validate(bytes);
            _newPicture = _picture = bytes;
            RefreshPicture();
            Info("The picture is saved with the contact.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { Fail("That picture could not be read: " + ex.Message); }
    }

    // ---------------------------------------------------------------- form state

    private void StashEmail() { _emails[_shownEmail] = _emailAddress.Text ?? ""; _emailNames[_shownEmail] = _displayAs.Text ?? ""; }

    private void StashAddress() => _addresses[AddressTypes[_shownAddress]] = _address.Text ?? "";

    private void StashPhone(int row)
    {
        if (_phones[row].Type.SelectedItem is ComboBoxItem { Content: string type } && PhoneTypes.Contains(type))
            _phoneValues[type] = _phones[row].Value.Text ?? "";
    }

    private void ShowPhone(int row)
    {
        var type = (_phones[row].Type.SelectedItem as ComboBoxItem)?.Content as string ?? "";
        var supported = PhoneTypes.Contains(type);
        _phones[row].Value.IsEnabled = supported;
        _phones[row].Value.Text = supported ? _phoneValues.GetValueOrDefault(type, "") : "";
        ToolTip.SetTip(_phones[row].Value, supported ? null : $"{type}: {PlannedFeature} (Hotmail contacts have no field for it)");
        _phones[row].Value.Watermark = supported ? null : PlannedFeature;
    }

    private static void SelectType(ComboBox box, string type) =>
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Content == type);

    private void RefreshFileAs(string? keep = null)
    {
        var current = keep ?? _fileAs.SelectedItem as string;
        var name = ContactFormat.ParseFullName(_fullName.Text);
        var choices = ContactFormat.FileAsChoices(name.Given, name.Surname, _company.Text ?? "").ToList();
        if (!string.IsNullOrWhiteSpace(current) && !choices.Contains(current!)) choices.Insert(0, current!);
        _loading = true;
        _fileAs.ItemsSource = choices;
        _fileAs.SelectedItem = current is not null && choices.Contains(current) ? current : choices.FirstOrDefault();
        _loading = false;
    }

    private void RefreshPicture()
    {
        Control content;
        if (_picture is not null)
        {
            try { content = new Image { Source = new Bitmap(new MemoryStream(_picture)), Stretch = Stretch.UniformToFill }; }
            catch (Exception) { content = Silhouette(); }
        }
        else content = Silhouette();
        _pictureButton.Content = new Border { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), ClipToBounds = true, Child = content };
    }

    /// <summary>Outlook's grey placeholder: a white head and shoulders on grey.</summary>
    private static Control Silhouette()
    {
        var grid = new Grid { Background = new SolidColorBrush(Color.FromRgb(0xB5, 0xB5, 0xB5)), ClipToBounds = true };
        grid.Children.Add(new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 56, Height = 62, Fill = Brushes.White, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 22, 0, 0)
        });
        grid.Children.Add(new Border
        {
            Width = 112, Height = 56, Background = Brushes.White, CornerRadius = new CornerRadius(56, 56, 0, 0),
            VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, -6)
        });
        return grid;
    }

    private void RefreshCard()
    {
        if (_loading) return;
        _card.Children.Clear();
        var c = Collect();
        void Line(string text, double size = 12, bool bold = false)
        {
            if (text.Trim().Length == 0) return;
            _card.Children.Add(new TextBlock { Text = text.Trim(), FontSize = size, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, TextTrimming = TextTrimming.CharacterEllipsis });
        }
        Line(ContactFormat.ShownName(c) == "(no name)" ? "" : ContactFormat.ShownName(c), 15, true);
        Line(c.CompanyName);
        Line(c.JobTitle);
        foreach (var p in c.BusinessPhones) Line(p + " Work");
        foreach (var p in c.HomePhones) Line(p + " Home");
        Line(c.MobilePhone.Length > 0 ? c.MobilePhone + " Mobile" : "");
        foreach (var e in c.Emails) Line(e.Address);
        Line(ContactFormat.FormatAddress(c.BusinessAddress.IsEmpty ? c.HomeAddress : c.BusinessAddress).Replace("\n", ", "));
        Line(c.BusinessHomePage);
    }

    private void UpdateTitle()
    {
        var name = (_fullName.Text ?? "").Trim();
        if (name.Length == 0) name = (_company.Text ?? "").Trim();
        Title = (name.Length == 0 ? "Untitled" : name) + " - Contact";
    }

    // ---------------------------------------------------------------- layout

    private Control BuildLayout()
    {
        var root = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3)) };
        var top = new StackPanel();
        top.Children.Add(QuickAccess());
        top.Children.Add(RibbonHost());
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);
        root.Children.Add(new Border
        {
            Margin = new Thickness(8, 4, 8, 4), Background = Brushes.White, BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4), Child = new ScrollViewer { Content = Form() }
        });
        return root;
    }

    private Control QuickAccess()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(8, 4, 8, 0) };
        bar.Children.Add(Icon("OlIconAppMark", 22));
        bar.Children.Add(Small("OlIconSave", "Save & Close", SaveAndCloseAsync));
        bar.Children.Add(Small("OlIconUndo", "Undo", () => { (FocusManager?.GetFocusedElement() as TextBox)?.Undo(); return Task.CompletedTask; }));
        bar.Children.Add(Small("OlIconRedo", "Redo", () => { (FocusManager?.GetFocusedElement() as TextBox)?.Redo(); return Task.CompletedTask; }));
        bar.Children.Add(Small("OlIconSortUp", "Previous Item", null));
        var next = Small("OlIconSortUp", "Next Item", null);
        ((Control)next.Content!).RenderTransform = new RotateTransform(180);
        bar.Children.Add(next);
        return bar;
    }

    private Control RibbonHost()
    {
        var grid = new Grid { Margin = new Thickness(4, 0) };
        grid.Children.Add(Ribbon());
        var tellMe = new Button
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 8, 0),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children = { Icon("OlIconBulb", 16), new TextBlock { Text = "Tell me what you want to do", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center } }
            }
        };
        grid.Children.Add(Wire(tellMe, "Tell me", null));
        return grid;
    }

    private TabControl Ribbon()
    {
        var tabs = new TabControl { Padding = new Thickness(0), Background = Brushes.Transparent };
        tabs.Classes.Add("olTabs");
        void Tab(string name, params Control[] groups)
        {
            var strip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2) };
            foreach (var g in groups) strip.Children.Add(g);
            tabs.Items.Add(new TabItem
            {
                Header = name,
                Content = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = strip }
            });
        }
        Tab("File", Group("Actions", Big("OlIconSaveClose", "Save & Close", SaveAndCloseAsync), Big("OlIconPrint", "Print", null)));
        Tab("Contact",
            Group("Actions", Big("OlIconSaveClose", "Save & Close", SaveAndCloseAsync), Big("OlIconDelete", "Delete", DeleteAsync),
                Stack(Mini("OlIconSave", "Save & New", SaveAndNewAsync), Mini("OlIconForward", "Forward", null))),
            Group("Show", Toggle("OlIconPeople", "General"), Stack(Mini("OlIconReadingPane", "Details", null), Mini("OlIconLock", "Certificates", null), Mini("OlIconFilter", "All Fields", null))),
            Group("Communicate", Big("OlIconMail", "Email", EmailAsync), Big("OlIconCalendar", "Meeting", MeetingAsync), Big("OlIconMore", "More", null)),
            Group("Names", Big("OlIconAddressBook", "Address Book", null), Big("OlIconPeople", "Check Names", null)),
            Group("Options", Big("OlIconPeopleR", "Business Card", null), Big("OlIconPersonSilhouette", "Picture", ChoosePictureAsync)),
            Group("Tags", Stack(Mini("OlIconCategorize", "Categorize", null), Mini("OlIconFlag", "Follow Up", null), Mini("OlIconLock", "Private", null))),
            Group("Immersive", Big("OlIconReadAloud", "Immersive Reader", null)),
            Group("Zoom", Big("OlIconSearch", "Zoom", null)));
        Tab("Insert", Group("Include", Big("OlIconPaperclip", "Attach File", null), Big(null, "Outlook Item", null), Big("OlIconPeopleR", "Business Card", null), Big(null, "Signature", null)),
            Group("Tables", Big(null, "Table", null)), Group("Illustrations", Big(null, "Pictures", null), Big(null, "Shapes", null)),
            Group("Links", Big(null, "Link", null), Big(null, "Bookmark", null)), Group("Text", Big(null, "Text Box", null), Big(null, "Date & Time", null)),
            Group("Symbols", Big(null, "Equation", null), Big(null, "Symbol", null)));
        Tab("Format Text", Group("Font", Big(null, "Font", null), Big(null, "Styles", null)), Group("Paragraph", Big(null, "Paragraph", null)));
        Tab("Review", Group("Proofing", Big(null, "Spelling & Grammar", null), Big(null, "Thesaurus", null)),
            Group("Speech", Big("OlIconReadAloud", "Read Aloud", null)), Group("Language", Big("OlIconTranslate", "Translate", null)));
        Tab("Help", Group("Help", Big("OlIconHelp", "Help", null), Big(null, "Contact Support", null), Big("OlIconFeedback", "Feedback", null)));
        tabs.SelectedIndex = 1;
        return tabs;
    }

    private Control Form()
    {
        var page = new Grid { ColumnDefinitions = new ColumnDefinitions("700,*"), Margin = new Thickness(16, 14) };
        var left = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*,160"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto") };
        var row = 0;
        void Add(Control label, Control field, int span = 1)
        {
            Grid.SetRow(label, row); left.Children.Add(label);
            Grid.SetRow(field, row); Grid.SetColumn(field, 1); Grid.SetColumnSpan(field, span); left.Children.Add(field);
            row++;
        }
        void Heading(string text)
        {
            var t = new TextBlock { Text = text, Foreground = Brushes.Gray, Margin = new Thickness(0, 12, 0, 4) };
            Grid.SetRow(t, row++); Grid.SetColumnSpan(t, 3); left.Children.Add(t);
        }

        Add(Pill("Full Name...", "Check Full Name: " + PlannedFeature + ". Type the name; it is split into first, middle and last name when saved."), _fullName);
        Add(Label("Company"), _company);
        Add(Label("Job title"), _jobTitle);
        Add(Label("File as"), _fileAs);
        Grid.SetRow(_pictureButton, 0); Grid.SetColumn(_pictureButton, 2); Grid.SetRowSpan(_pictureButton, 4);
        _pictureButton.HorizontalAlignment = HorizontalAlignment.Right; _pictureButton.VerticalAlignment = VerticalAlignment.Top;
        ToolTip.SetTip(_pictureButton, "Add or change the contact's picture");
        _pictureButton.Click += async (_, _) => await ChoosePictureAsync();
        left.Children.Add(_pictureButton);

        Heading("Internet");
        foreach (var slot in new[] { "Email", "Email 2", "Email 3" }) _emailSlot.Items.Add(slot);
        _emailSlot.Margin = new Thickness(28, 3, 8, 3);
        _emailSlot.VerticalAlignment = VerticalAlignment.Center;
        ToolTip.SetTip(_emailSlot, "Which email address to show (each contact keeps up to three)");
        Add(_emailSlot, _emailAddress, 2);
        Add(Label("Display as"), _displayAs, 2);
        Add(Label("Web page address"), _webPage, 2);
        Add(Label("IM address"), _im, 2);

        Heading("Phone numbers");
        for (var i = 0; i < 4; i++)
        {
            var type = new ComboBox { Width = 140 };
            foreach (var t in PhoneTypes) type.Items.Add(new ComboBoxItem { Content = t });
            foreach (var t in UnsupportedPhoneTypes)
            {
                var item = new ComboBoxItem { Content = t };
                ToolTip.SetTip(item, $"{t}: {PlannedFeature}");
                type.Items.Add(item);
            }
            var value = Field();
            _phones[i] = (type, value);
            var index = i;
            var previous = "";
            type.DropDownOpened += (_, _) => previous = (type.SelectedItem as ComboBoxItem)?.Content as string ?? "";
            type.SelectionChanged += (_, _) =>
            {
                if (_loading) return;
                if (PhoneTypes.Contains(previous)) _phoneValues[previous] = value.Text ?? "";
                ShowPhone(index);
                previous = (type.SelectedItem as ComboBoxItem)?.Content as string ?? "";
            };
            value.TextChanged += (_, _) => { if (!_loading) { StashPhone(index); RefreshCard(); } };
            Add(type, value, 2);
        }

        Heading("Addresses");
        foreach (var t in AddressTypes) _addressType.Items.Add(t);
        var addressLeft = new StackPanel { Spacing = 10, Children = { _addressType, _mailing } };
        ToolTip.SetTip(_mailing, "Mailing address: " + PlannedFeature + " (Hotmail contacts do not mark one)");
        Grid.SetRow(addressLeft, row); left.Children.Add(addressLeft);
        Grid.SetRow(_address, row); Grid.SetColumn(_address, 1); _address.Margin = new Thickness(0, 0, 8, 0); left.Children.Add(_address);
        var mapIt = new Button
        {
            Width = 144, Height = 110, HorizontalAlignment = HorizontalAlignment.Right, Background = new SolidColorBrush(Color.FromRgb(0xF6, 0xF6, 0xF6)),
            Content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Spacing = 4, Children = { Icon("OlIconGlobe", 28), new TextBlock { Text = "Map It", Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center } } }
        };
        Wire(mapIt, "Map It", null);
        Grid.SetRow(mapIt, row); Grid.SetColumn(mapIt, 2); left.Children.Add(mapIt);
        row++;

        var right = new DockPanel { Margin = new Thickness(18, 0, 0, 0) };
        var cardBorder = new Border
        {
            Width = 372, Height = 222, HorizontalAlignment = HorizontalAlignment.Center, BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Colors.White, 0), new GradientStop(Color.FromRgb(0xEE, 0xF3, 0xF9), 1) }
            },
            Child = _card, Margin = new Thickness(0, 0, 0, 14)
        };
        ToolTip.SetTip(cardBorder, "Business card preview (editing the card: " + PlannedFeature + ")");
        DockPanel.SetDock(cardBorder, Dock.Top);
        right.Children.Add(cardBorder);
        var notesLabel = new TextBlock { Text = "Notes", Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(notesLabel, Dock.Top);
        right.Children.Add(notesLabel);
        _notes.MinHeight = 420;
        right.Children.Add(_notes);
        Grid.SetColumn(right, 1);
        page.Children.Add(left);
        page.Children.Add(right);

        _emailSlot.SelectionChanged += (_, _) =>
        {
            if (_loading || _emailSlot.SelectedIndex < 0) return;
            StashEmail();
            _shownEmail = _emailSlot.SelectedIndex;
            _loading = true;
            _emailAddress.Text = _emails[_shownEmail];
            _displayAs.Text = _emailNames[_shownEmail];
            _loading = false;
        };
        _addressType.SelectionChanged += (_, _) =>
        {
            if (_loading || _addressType.SelectedIndex < 0) return;
            StashAddress();
            _shownAddress = _addressType.SelectedIndex;
            _loading = true;
            _address.Text = _addresses.GetValueOrDefault(AddressTypes[_shownAddress], "");
            _loading = false;
        };
        _fullName.TextChanged += (_, _) => { if (!_loading) { RefreshFileAs(); RefreshCard(); UpdateTitle(); } };
        _company.TextChanged += (_, _) => { if (!_loading) { RefreshFileAs(); RefreshCard(); UpdateTitle(); } };
        foreach (var box in new[] { _jobTitle, _emailAddress, _webPage, _address })
            box.TextChanged += (_, _) => { if (!_loading) RefreshCard(); };
        _emailAddress.LostFocus += (_, _) =>
        {
            // Outlook fills Display as from the name and address when it is left empty
            if (string.IsNullOrWhiteSpace(_displayAs.Text) && !string.IsNullOrWhiteSpace(_emailAddress.Text)) _displayAs.Text = DefaultDisplayAs(_emailAddress.Text!.Trim());
        };
        return page;
    }

    private async Task EmailAsync()
    {
        var c = Collect();
        var first = c.Emails.FirstOrDefault();
        if (first is null) { Info("Add an email address first."); return; }
        if (_email is null) { Info("Email: " + PlannedFeature.ToLowerInvariant() + "."); return; }
        _email(ContactFormat.ShownName(c), first.Address);
        await Task.CompletedTask;
    }

    private async Task MeetingAsync()
    {
        var first = Collect().Emails.FirstOrDefault();
        if (first is null) { Info("Add an email address first."); return; }
        if (_meeting is null) { Info("Meeting: " + PlannedFeature.ToLowerInvariant() + "."); return; }
        await _meeting(first.Address);
    }

    // ---------------------------------------------------------------- building blocks

    private static TextBox Field() => new() { MinHeight = 30, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3) };

    private static TextBlock Label(string text) => new() { Text = text, Foreground = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(28, 0, 8, 0) };

    private Button Pill(string text, string tip)
    {
        var b = new Button { Content = text, MinWidth = 112, Margin = new Thickness(28, 3, 8, 3), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => Info(tip);
        return b;
    }

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
        var content = new StackPanel { Width = 66, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(icon is not null ? Icon(icon, 28) : new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(4), BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center });
        content.Children.Add(new TextBlock { Text = caption, FontSize = 11, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center });
        return Wire(new Button { Content = content, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(2) }, caption, action);
    }

    private Button Toggle(string icon, string caption)
    {
        var b = Big(icon, caption, () => Task.CompletedTask);
        b.Background = new SolidColorBrush(Color.FromRgb(0xE1, 0xE1, 0xE1));
        ToolTip.SetTip(b, "General page (shown)");
        return b;
    }

    private Button Mini(string? icon, string caption, Func<Task>? action)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        if (icon is not null) content.Children.Add(Icon(icon, 16));
        content.Children.Add(new TextBlock { Text = caption, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        return Wire(new Button { Content = content, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(4, 2), MinHeight = 22 }, caption, action);
    }

    private Button Small(string icon, string caption, Func<Task>? action) =>
        Wire(new Button { Content = Icon(icon, 18), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(4) }, caption, action);

    private Button Wire(Button b, string caption, Func<Task>? action)
    {
        if (action is null)
        {
            ToolTip.SetTip(b, PlannedFeature);
            b.Click += (_, _) => Info($"{caption}: {PlannedFeature.ToLowerInvariant()}.");
        }
        else
        {
            ToolTip.SetTip(b, caption);
            b.Click += async (_, _) => await action();
        }
        return b;
    }

    private static StackPanel Stack(params Control[] rows)
    {
        var s = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        foreach (var r in rows) s.Children.Add(r);
        return s;
    }

    private static Control Icon(string key, double size, IBrush? fill = null)
    {
        if (Application.Current is { } app && app.TryFindResource(key, out var template) && template is IControlTemplate ct)
        {
            var control = new ContentControl { Template = ct, Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center };
            if (fill is not null) control.Foreground = fill;
            return control;
        }
        return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(3), BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1) };
    }

    private void Info(string message) { _status.Foreground = Brushes.Gray; _status.Text = message; }

    private void Fail(string message) { _status.Foreground = Brushes.Firebrick; _status.Text = message; }
}
