using System.Net;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenOutlook.Auth;
using OpenOutlook.Desktop;
using OpenOutlook.Providers.Microsoft;
using Xunit;

namespace OpenOutlook.HeadlessTests;

public sealed class PeoplePageHeadlessTests
{
    private const string Contacts = """
        {"value":[
          {"id":"c-bryan","displayName":"Michael Bryan","givenName":"Michael","surname":"Bryan","emailAddresses":[{"address":"michael.bryan@example.test","name":"Michael Bryan"}]},
          {"id":"c-aggro","displayName":"Aggronaught","givenName":"Aggronaught","mobilePhone":"+15142375416"},
          {"id":"c-home","displayName":"Gig Harbor Home","fileAs":"Gig Harbor Home","homePhones":["1 253-313-5728"],
           "homeAddress":{"street":"1 Harbor Way","city":"Gig Harbor","state":"WA","postalCode":"98335","countryOrRegion":""},"personalNotes":"Gate code 1234"},
          {"id":"c-dalman","displayName":"Christian Dalman","givenName":"Christian","surname":"Dalman","companyName":"Contoso","jobTitle":"Chef"}
        ]}
        """;

    private static (HttpClient Http, List<string> Calls) FakeGraph(string account)
    {
        var calls = new List<string>();
        var http = new HttpClient(new Handler(request =>
        {
            calls.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            var response = request.RequestUri.AbsolutePath switch
            {
                "/v1.0/me" => Json("{\"id\":\"" + account + "\"}"),
                "/v1.0/me/contactFolders" => Json("""{"value":[{"id":"fam","displayName":"Family"}]}"""),
                "/v1.0/me/contacts" => Json(Contacts),
                "/v1.0/me/contactFolders/fam/contacts" => Json("""{"value":[]}"""),
                var p when p.EndsWith("/photo/$value") => new HttpResponseMessage(HttpStatusCode.NotFound),
                _ => throw new Exception("Unexpected Graph endpoint " + request.RequestUri.AbsolutePath)
            };
            response.RequestMessage = request;
            return response;
        }));
        return (http, calls);
    }

    private static ConnectedAccount Hotmail(string id, string[]? scopes = null) =>
        new(OAuthProvider.MicrosoftConsumers, id, "person@hotmail.test", "client", DateTimeOffset.UtcNow, scopes ?? MicrosoftAccountPermissions.PlannedPersonalScopes);

    [AvaloniaFact]
    public async Task Lists_folders_and_contacts_in_outlook_order_with_index_search_details_and_list_view()
    {
        var account = "people-" + Guid.NewGuid().ToString("N");
        var (http, _) = FakeGraph(account);
        using var _http = http;
        var owner = new Window { Width = 1500, Height = 900 };
        var emailed = new List<string>();
        var page = new PeoplePage(owner, http, _ => Task.FromResult("token-" + account), _ => Task.CompletedTask, null,
            (a, name, address) => emailed.Add(address), (a, address) => Task.CompletedTask);
        owner.Content = new DockPanel { Children = { page.Ribbon, page } };
        DockPanel.SetDock(page.Ribbon, Dock.Top);
        page.SetAccounts([new ConnectedAccount(OAuthProvider.Google, "g", "person@gmail.test", "client", DateTimeOffset.UtcNow), Hotmail(account)]);
        owner.Show();
        try
        {
            await page.ActivateAsync();
            await WaitUntil(() => page.Contacts.Count == 4);
            // Outlook files people by "Last, First": Aggronaught, Bryan, Dalman, Gig Harbor Home
            Assert.Equal(["c-aggro", "c-bryan", "c-dalman", "c-home"], page.Contacts.Select(c => c.Id));
            var lists = page.GetVisualDescendants().OfType<ListBox>().ToList();
            Assert.Equal(2, lists[0].ItemCount);                                              // Contacts, Family
            var list = lists[1];
            Assert.Equal(4, list.ItemCount);
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Items: 4");

            page.JumpTo("e");                                                                  // no E: the index moves on to the next name (Gig Harbor Home)
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("c-home", ((list.SelectedItem as ListBoxItem)?.Tag as GraphContact)?.Id);
            var details = string.Join("|", page.GetVisualDescendants().OfType<SelectableTextBlock>().Select(t => t.Text));
            Assert.Contains("1 253-313-5728", details);
            Assert.Contains("Gig Harbor, WA 98335", details);
            Assert.Contains("Gate code 1234", details);

            var search = page.Ribbon.GetVisualDescendants().OfType<TextBox>().Single(t => t.Watermark == "Search People");
            search.Text = "contoso";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, list.ItemCount);
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Items: 1 of 4");
            search.Text = "";
            Dispatcher.UIThread.RunJobs();

            page.JumpTo("b");
            Dispatcher.UIThread.RunJobs();
            Button(page.Ribbon, "Email").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Assert.Equal(["michael.bryan@example.test"], emailed);

            var planned = Button(page.Ribbon, "Mail Merge");
            Assert.Equal(PeoplePage.PlannedFeature, ToolTip.GetTip(planned));
            Shot(owner, "people-page");

            Button(page.Ribbon, "List").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(list.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Contoso");
            Shot(owner, "people-list-view");
        }
        finally { page.Deactivate(); owner.Close(); }
    }

    [AvaloniaFact]
    public async Task An_account_without_contact_access_is_told_to_repair_its_sign_in()
    {
        var account = "people-" + Guid.NewGuid().ToString("N");
        var (http, calls) = FakeGraph(account);
        using var _http = http;
        var owner = new Window { Width = 1200, Height = 800 };
        var page = new PeoplePage(owner, http, _ => Task.FromResult("t"), _ => Task.CompletedTask, null, (_, _, _) => { }, (_, _) => Task.CompletedTask);
        owner.Content = page;
        page.SetAccounts([Hotmail(account, ["offline_access", "User.Read", "Mail.ReadWrite"])]);
        owner.Show();
        try
        {
            await page.ActivateAsync();
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Repair this account's sign-in") == true);
            Assert.Empty(calls);                                                               // nothing is asked of Graph without the permission
        }
        finally { page.Deactivate(); owner.Close(); }
    }

    [AvaloniaFact]
    public async Task Contact_window_reads_the_form_back_into_a_contact_and_saves_through_the_callback()
    {
        GraphContact? saved = null;
        var existing = new GraphContact
        {
            Id = "c1", DisplayName = "Ann Example", GivenName = "Ann", Surname = "Example", CompanyName = "Contoso",
            Emails = [new("ann@example.test", "Ann Example (ann@example.test)")], BusinessPhones = ["+1 555 0100"], MobilePhone = "+1 555 0101",
            HomeAddress = new GraphPostalAddress("2 Side St", "Tacoma", "WA", "98402", ""), Birthday = new DateOnly(1980, 5, 17), PersonalNotes = "notes"
        };
        var window = new ContactEditorWindow(existing, null, (c, picture) => { saved = c; return Task.FromResult<string?>(null); });
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Ann Example - Contact", window.Title);
            var boxes = window.GetVisualDescendants().OfType<TextBox>().ToList();
            var fullName = boxes[0];
            Assert.Equal("Ann Example", fullName.Text);
            var fileAs = window.GetVisualDescendants().OfType<ComboBox>().First();
            Assert.Equal("Example, Ann", fileAs.SelectedItem);
            Assert.Contains("Contoso (Example, Ann)", fileAs.Items.OfType<string>());

            fullName.Text = "Dr. Ann B. Example";
            var round = window.Collect();
            Assert.Equal(("Dr.", "Ann", "B.", "Example"), (round.Title, round.GivenName, round.MiddleName, round.Surname));
            Assert.Equal(["+1 555 0100"], round.BusinessPhones);
            Assert.Equal("+1 555 0101", round.MobilePhone);
            Assert.Equal("Tacoma", round.HomeAddress.City);                                    // the Home address was shown and read back
            Assert.Equal(new DateOnly(1980, 5, 17), round.Birthday);                            // fields not on the General page are kept
            Assert.Equal("c1", round.Id);

            Assert.Equal("\"not an address\" is not a valid email address.",
                ContactEditorWindow.Problem(round with { Emails = [new("not an address", "")] }));
            Assert.NotNull(ContactEditorWindow.Problem(new GraphContact()));

            var saveClose = window.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) as string == "Save & Close");
            saveClose.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            await WaitUntil(() => saved is not null);
            Assert.Equal("Example", saved!.Surname);
            Assert.True(window.Saved);
            Assert.Equal(ContactEditorWindow.PlannedFeature,
                ToolTip.GetTip(window.GetVisualDescendants().OfType<Button>().First(b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Business Card"))));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void New_contact_window_looks_like_outlook()
    {
        var window = new ContactEditorWindow(null, null, (_, _) => Task.FromResult<string?>(null));
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Untitled - Contact", window.Title);
            var phoneTypes = window.GetVisualDescendants().OfType<ComboBox>().Where(c => c.Items.OfType<ComboBoxItem>().Any(i => (string?)i.Content == "Mobile")).ToList();
            Assert.Equal(4, phoneTypes.Count);
            Assert.Equal(["Business", "Home", "Business Fax", "Mobile"], phoneTypes.Select(c => (string?)((ComboBoxItem)c.SelectedItem!).Content));
            Shot(window, "contact-window");
        }
        finally { window.Close(); }
    }

    private static Button Button(Control root, string caption) =>
        root.GetVisualDescendants().OfType<Button>().First(b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Replace("\n", " ") == caption));

    private static void Shot(Window w, string name)
    {
        var dir = Environment.GetEnvironmentVariable("OO_HEADLESS_OUT");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        w.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        w.CaptureRenderedFrame()?.Save(Path.Combine(dir, name + ".png"));
    }

    private static async Task WaitUntil(Func<bool> condition, int ms = 10000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("condition not met");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
