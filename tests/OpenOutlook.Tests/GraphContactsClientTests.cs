using System.Net;
using System.Text.Json;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Tests;

public sealed class GraphContactsClientTests
{
    // Every test uses its own account id and token: GraphAccountVerification caches verified pairs process-wide.
    private static string NewAccount() => "contacts-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Lists_the_default_folder_first_then_the_others_and_reads_every_page_of_contacts()
    {
        var account = NewAccount();
        var calls = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            calls.Add(request.Method + " " + request.RequestUri!.PathAndQuery);
            var path = request.RequestUri.AbsolutePath;
            if (path == "/v1.0/me") return Json(HttpStatusCode.OK, "{\"id\":\"" + account + "\"}");
            if (path == "/v1.0/me/contactFolders") return Json(HttpStatusCode.OK, """{"value":[{"id":"f1","displayName":"Family"}]}""");
            if (path == "/v1.0/me/contacts" && !request.RequestUri.Query.Contains("skip"))
                return Json(HttpStatusCode.OK, """
                    {"value":[{"id":"c1","displayName":"Ann Example","givenName":"Ann","surname":"Example","companyName":"Contoso",
                      "emailAddresses":[{"address":"ann@example.test","name":"Ann Example"}],"businessPhones":["+1 555 0100"],"homePhones":[],
                      "mobilePhone":"+1 555 0101","businessAddress":{"street":"1 Main St","city":"Seattle","state":"WA","postalCode":"98101","countryOrRegion":"USA"},
                      "personalNotes":"line one\nline two","birthday":"1980-05-17T00:00:00Z"}],
                     "@odata.nextLink":"https://graph.microsoft.com/v1.0/me/contacts?$skip=100"}
                    """);
            if (path == "/v1.0/me/contacts") return Json(HttpStatusCode.OK, """{"value":[{"id":"c2","displayName":"Bob"}]}""");
            throw new Exception("Unexpected Graph endpoint " + path);
        }));
        var client = new GraphContactsClient(http, account);
        var folders = await client.ListFoldersAsync("token-" + account);
        Assert.Equal(["Contacts", "Family"], folders.Select(f => f.Name));
        Assert.True(folders[0].IsDefault);
        var contacts = await client.ListContactsAsync("token-" + account, folders[0]);
        Assert.Equal(["c1", "c2"], contacts.Select(c => c.Id));
        var ann = contacts[0];
        Assert.Equal("ann@example.test", Assert.Single(ann.Emails).Address);
        Assert.Equal("+1 555 0100", Assert.Single(ann.BusinessPhones));
        Assert.Equal("Seattle", ann.BusinessAddress.City);
        Assert.Equal("line one\nline two", ann.PersonalNotes);                     // line breaks survive in notes
        Assert.Equal(new DateOnly(1980, 5, 17), ann.Birthday);
        Assert.Equal("GET /v1.0/me?$select=id", calls[0]);                           // the token is checked against the account before anything is read
    }

    [Fact]
    public async Task Creates_in_a_folder_updates_deletes_and_reads_a_missing_photo_as_none()
    {
        var account = NewAccount();
        var seen = new List<string>();
        JsonDocument? created = null, patched = null;
        using var http = new HttpClient(new Handler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            seen.Add(request.Method + " " + path);
            if (path == "/v1.0/me") return Json(HttpStatusCode.OK, "{\"id\":\"" + account + "\"}");
            if (request.Method == HttpMethod.Post && path == "/v1.0/me/contactFolders/f1/contacts")
            {
                created = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                return Json(HttpStatusCode.Created, """{"id":"new","displayName":"Cy Example","givenName":"Cy","surname":"Example"}""");
            }
            if (request.Method == HttpMethod.Patch && path == "/v1.0/me/contacts/new")
            {
                patched = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                return Json(HttpStatusCode.OK, """{"id":"new","displayName":"Cy Example","jobTitle":"Chef"}""");
            }
            if (request.Method == HttpMethod.Get && path == "/v1.0/me/contacts/new/photo/$value") return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (request.Method == HttpMethod.Delete && path == "/v1.0/me/contacts/new") return new HttpResponseMessage(HttpStatusCode.NoContent);
            throw new Exception("Unexpected Graph endpoint " + request.Method + " " + path);
        }));
        var client = new GraphContactsClient(http, account);
        var token = "token-" + account;
        var draft = new GraphContact
        {
            DisplayName = "Cy Example", GivenName = "Cy", Surname = "Example", Emails = [new("cy@example.test", "Cy Example")],
            MobilePhone = "+1 555 0102", HomeAddress = new GraphPostalAddress("2 Side St", "Tacoma", "WA", "98402", "USA"),
            Birthday = new DateOnly(1990, 1, 2)
        };
        var saved = await client.CreateContactAsync(token, new GraphContactFolder("f1", "Family"), draft);
        Assert.Equal("new", saved.Id);
        Assert.Equal("cy@example.test", created!.RootElement.GetProperty("emailAddresses")[0].GetProperty("address").GetString());
        Assert.Equal("Tacoma", created.RootElement.GetProperty("homeAddress").GetProperty("city").GetString());
        Assert.Equal("1990-01-02T00:00:00Z", created.RootElement.GetProperty("birthday").GetString());
        var updated = await client.UpdateContactAsync(token, saved with { JobTitle = "Chef", Emails = draft.Emails });
        Assert.Equal("Chef", updated.JobTitle);
        Assert.Equal("Chef", patched!.RootElement.GetProperty("jobTitle").GetString());
        Assert.Null(await client.GetPhotoAsync(token, "new"));
        await client.DeleteContactAsync(token, "new");
        Assert.Equal("DELETE /v1.0/me/contacts/new", seen[^1]);
        created.Dispose(); patched.Dispose();
    }

    [Fact]
    public async Task Refuses_bad_contacts_and_unsafe_page_links()
    {
        var account = NewAccount();
        using var http = new HttpClient(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1.0/me") return Json(HttpStatusCode.OK, "{\"id\":\"" + account + "\"}");
            if (path == "/v1.0/me/contacts")
                return Json(HttpStatusCode.OK, """{"value":[],"@odata.nextLink":"https://evil.example/v1.0/me/contacts?$skip=100"}""");
            throw new Exception("Unexpected Graph endpoint " + path);
        }));
        var client = new GraphContactsClient(http, account);
        var token = "token-" + account;
        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateContactAsync(token, new GraphContactFolder("", "Contacts"), new GraphContact()));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateContactAsync(token, new GraphContactFolder("", "Contacts"),
            new GraphContact { DisplayName = "X", Emails = [new("not-an-address", "")] }));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SetPhotoAsync(token, "c1", "GIF89a....."u8.ToArray()));
        await Assert.ThrowsAsync<GraphMailException>(() => client.ListContactsAsync(token, new GraphContactFolder("", "Contacts")));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body) };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : this(request => Task.FromResult(respond(request))) { }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await respond(request);
            response.RequestMessage ??= request;
            return response;
        }
    }
}
