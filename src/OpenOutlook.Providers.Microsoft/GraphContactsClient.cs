using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace OpenOutlook.Providers.Microsoft;

/// <summary>A contact folder. <see cref="Id"/> is empty for the account's default Contacts folder (Graph serves it at /me/contacts).</summary>
public sealed record GraphContactFolder(string Id, string Name)
{
    public bool IsDefault => Id.Length == 0;
}

public sealed record GraphEmailAddress(string Address, string Name);

/// <summary>A postal address as Graph stores it (physicalAddress).</summary>
public sealed record GraphPostalAddress(string Street, string City, string State, string PostalCode, string CountryOrRegion)
{
    public static readonly GraphPostalAddress Empty = new("", "", "", "", "");
    public bool IsEmpty => Street.Length + City.Length + State.Length + PostalCode.Length + CountryOrRegion.Length == 0;
}

/// <summary>The fields of a personal contact that OpenOutlook reads and writes. Graph v1.0 has no fax numbers.</summary>
public sealed record GraphContact
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string GivenName { get; init; } = "";
    public string MiddleName { get; init; } = "";
    public string Surname { get; init; } = "";
    public string Title { get; init; } = "";
    public string Suffix { get; init; } = "";
    public string FileAs { get; init; } = "";
    public string CompanyName { get; init; } = "";
    public string JobTitle { get; init; } = "";
    public IReadOnlyList<GraphEmailAddress> Emails { get; init; } = [];
    public IReadOnlyList<string> BusinessPhones { get; init; } = [];
    public IReadOnlyList<string> HomePhones { get; init; } = [];
    public string MobilePhone { get; init; } = "";
    public GraphPostalAddress BusinessAddress { get; init; } = GraphPostalAddress.Empty;
    public GraphPostalAddress HomeAddress { get; init; } = GraphPostalAddress.Empty;
    public GraphPostalAddress OtherAddress { get; init; } = GraphPostalAddress.Empty;
    public string BusinessHomePage { get; init; } = "";
    public IReadOnlyList<string> ImAddresses { get; init; } = [];
    public string PersonalNotes { get; init; } = "";
    public DateOnly? Birthday { get; init; }
}

/// <summary>Personal Microsoft contacts through a no-redirect Graph transport (Contacts.ReadWrite).</summary>
public sealed class GraphContactsClient(HttpClient http, string expectedAccountId)
{
    private const string Origin = "https://graph.microsoft.com/v1.0";
    private const int MaxJsonBytes = 4 * 1024 * 1024;
    private const int MaxPages = 50;                       // 50 pages of 100: 5,000 contacts per folder
    public const int MaxPhotoBytes = 4 * 1024 * 1024;

    private const string Select = "id,displayName,givenName,middleName,surname,title,generation,fileAs,companyName,jobTitle," +
        "emailAddresses,businessPhones,homePhones,mobilePhone,businessAddress,homeAddress,otherAddress,businessHomePage,imAddresses," +
        "personalNotes,birthday";

    public async Task<IReadOnlyList<GraphContactFolder>> ListFoldersAsync(string token, CancellationToken ct = default)
    {
        await VerifyAsync(token, ct).ConfigureAwait(false);
        var folders = new List<GraphContactFolder> { new("", "Contacts") };
        var url = Origin + "/me/contactFolders?$select=id,displayName&$top=100";
        for (var page = 0; url is not null && page < MaxPages; page++)
        {
            using var json = await RequestAsync(HttpMethod.Get, url, token, null, ct).ConfigureAwait(false);
            foreach (var item in Value(json.RootElement))
                folders.Add(new GraphContactFolder(Required(item, "id", 2048), Optional(item, "displayName", 512) ?? "(unnamed)"));
            url = NextLink(json.RootElement);
        }
        if (url is not null) throw new GraphMailException("Graph returned too many contact folder pages.");
        return folders;
    }

    public async Task<IReadOnlyList<GraphContact>> ListContactsAsync(string token, GraphContactFolder folder, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (!folder.IsDefault) ValidateId(folder.Id);
        await VerifyAsync(token, ct).ConfigureAwait(false);
        var contacts = new List<GraphContact>();
        var url = CollectionUrl(folder) + "?$select=" + Select + "&$top=100";
        for (var page = 0; url is not null && page < MaxPages; page++)
        {
            using var json = await RequestAsync(HttpMethod.Get, url, token, null, ct).ConfigureAwait(false);
            foreach (var item in Value(json.RootElement)) contacts.Add(Read(item));
            url = NextLink(json.RootElement);
        }
        if (url is not null) throw new GraphMailException("Graph returned too many contact pages.");
        return contacts;
    }

    public async Task<GraphContact> CreateContactAsync(string token, GraphContactFolder folder, GraphContact contact, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (!folder.IsDefault) ValidateId(folder.Id);
        Validate(contact);
        await VerifyAsync(token, ct).ConfigureAwait(false);
        using var json = await RequestAsync(HttpMethod.Post, CollectionUrl(folder), token, Payload(contact), ct).ConfigureAwait(false);
        return Read(json.RootElement);
    }

    public async Task<GraphContact> UpdateContactAsync(string token, GraphContact contact, CancellationToken ct = default)
    {
        Validate(contact);
        ValidateId(contact.Id);
        await VerifyAsync(token, ct).ConfigureAwait(false);
        using var json = await RequestAsync(HttpMethod.Patch, Origin + "/me/contacts/" + Uri.EscapeDataString(contact.Id), token,
            Payload(contact), ct).ConfigureAwait(false);
        return Read(json.RootElement);
    }

    public async Task DeleteContactAsync(string token, string contactId, CancellationToken ct = default)
    {
        ValidateId(contactId);
        await VerifyAsync(token, ct).ConfigureAwait(false);
        using var _ = await RequestAsync(HttpMethod.Delete, Origin + "/me/contacts/" + Uri.EscapeDataString(contactId), token, null, ct)
            .ConfigureAwait(false);
    }

    /// <summary>The contact's picture (JPEG or PNG bytes), or null when it has none.</summary>
    public async Task<byte[]?> GetPhotoAsync(string token, string contactId, CancellationToken ct = default)
    {
        ValidateId(contactId);
        await VerifyAsync(token, ct).ConfigureAwait(false);
        var uri = SafeUri(Origin + "/me/contacts/" + Uri.EscapeDataString(contactId) + "/photo/$value");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CheckedToken(token));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri != uri || (int)response.StatusCode is >= 300 and < 400)
            throw new GraphMailException("Graph redirected a contact photo request.");
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
            throw new GraphMailException($"Graph contact photo request failed with HTTP {(int)response.StatusCode}.", response.StatusCode);
        if (response.Content.Headers.ContentLength > MaxPhotoBytes) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int count;
        while ((count = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > MaxPhotoBytes) return null;
            buffer.Write(chunk, 0, count);
        }
        return buffer.Length == 0 ? null : buffer.ToArray();
    }

    /// <summary>Replaces the contact's picture (JPEG or PNG, at most 4 MB).</summary>
    public async Task SetPhotoAsync(string token, string contactId, byte[] image, CancellationToken ct = default)
    {
        ValidateId(contactId);
        ArgumentNullException.ThrowIfNull(image);
        var type = image.Length > 3 && image[0] == 0xff && image[1] == 0xd8 ? "image/jpeg"
            : image.Length > 8 && image[0] == 137 && image[1] == 80 && image[2] == 78 && image[3] == 71 ? "image/png"
            : throw new ArgumentException("A contact picture must be a JPEG or PNG image.", nameof(image));
        if (image.Length > MaxPhotoBytes) throw new ArgumentException("A contact picture must be 4 MB or smaller.", nameof(image));
        await VerifyAsync(token, ct).ConfigureAwait(false);
        var uri = SafeUri(Origin + "/me/contacts/" + Uri.EscapeDataString(contactId) + "/photo/$value");
        using var request = new HttpRequestMessage(HttpMethod.Put, uri) { Content = new ByteArrayContent(image) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CheckedToken(token));
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri != uri || (int)response.StatusCode is >= 300 and < 400)
            throw new GraphMailException("Graph redirected a contact photo upload.");
        if (!response.IsSuccessStatusCode)
            throw new GraphMailException($"Graph contact photo upload failed with HTTP {(int)response.StatusCode}.", response.StatusCode);
    }

    // ---- request / response ---------------------------------------------------------------------------------------------------------

    private static string CollectionUrl(GraphContactFolder folder) =>
        folder.IsDefault ? Origin + "/me/contacts" : Origin + "/me/contactFolders/" + Uri.EscapeDataString(folder.Id) + "/contacts";

    private static void Validate(GraphContact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);
        string[] names = [contact.DisplayName, contact.GivenName, contact.MiddleName, contact.Surname, contact.CompanyName];
        if (names.All(string.IsNullOrWhiteSpace) && contact.Emails.Count == 0)
            throw new ArgumentException("A contact needs a name, a company or an email address.", nameof(contact));
        foreach (var text in names.Concat([contact.Title, contact.Suffix, contact.FileAs, contact.JobTitle, contact.MobilePhone, contact.BusinessHomePage]))
            if (text.Length > 1024 || text.Any(char.IsControl))
                throw new ArgumentException("A contact field is too long or holds control characters.", nameof(contact));
        if (contact.Emails.Count > 3 || contact.Emails.Any(e => e.Address.Length is 0 or > 320 || !e.Address.Contains('@') ||
                e.Address.Any(char.IsWhiteSpace) || e.Name.Length > 512 || e.Name.Any(char.IsControl)))
            throw new ArgumentException("A contact holds at most three valid email addresses.", nameof(contact));
        if (contact.BusinessPhones.Count > 2 || contact.HomePhones.Count > 2 || contact.ImAddresses.Count > 3 ||
            contact.BusinessPhones.Concat(contact.HomePhones).Concat(contact.ImAddresses).Any(p => p.Length is 0 or > 128 || p.Any(char.IsControl)))
            throw new ArgumentException("Invalid phone numbers or IM addresses.", nameof(contact));
        if (contact.PersonalNotes.Length > 32_768)
            throw new ArgumentException("The notes are too long.", nameof(contact));
        foreach (var address in new[] { contact.BusinessAddress, contact.HomeAddress, contact.OtherAddress })
            if (address.Street.Length > 1024 || new[] { address.City, address.State, address.PostalCode, address.CountryOrRegion }.Any(p => p.Length > 256 || p.Any(char.IsControl)))
                throw new ArgumentException("An address is too long.", nameof(contact));
    }

    private static object Payload(GraphContact c)
    {
        object Address(GraphPostalAddress a) => new { street = a.Street, city = a.City, state = a.State, postalCode = a.PostalCode, countryOrRegion = a.CountryOrRegion };
        return new Dictionary<string, object?>
        {
            ["displayName"] = c.DisplayName.Trim(),
            ["givenName"] = c.GivenName.Trim(),
            ["middleName"] = c.MiddleName.Trim(),
            ["surname"] = c.Surname.Trim(),
            ["title"] = c.Title.Trim(),
            ["generation"] = c.Suffix.Trim(),
            ["fileAs"] = c.FileAs.Trim(),
            ["companyName"] = c.CompanyName.Trim(),
            ["jobTitle"] = c.JobTitle.Trim(),
            ["emailAddresses"] = c.Emails.Select(e => new { address = e.Address.Trim(), name = e.Name.Trim() }).ToArray(),
            ["businessPhones"] = c.BusinessPhones.Select(p => p.Trim()).ToArray(),
            ["homePhones"] = c.HomePhones.Select(p => p.Trim()).ToArray(),
            ["mobilePhone"] = c.MobilePhone.Trim(),
            ["businessAddress"] = Address(c.BusinessAddress),
            ["homeAddress"] = Address(c.HomeAddress),
            ["otherAddress"] = Address(c.OtherAddress),
            ["businessHomePage"] = c.BusinessHomePage.Trim(),
            ["imAddresses"] = c.ImAddresses.Select(p => p.Trim()).ToArray(),
            ["personalNotes"] = c.PersonalNotes,
            // Graph keeps a birthday as a date at midnight UTC
            ["birthday"] = c.Birthday is { } b ? b.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00Z" : null
        };
    }

    private static GraphContact Read(JsonElement item)
    {
        GraphPostalAddress Address(string name)
        {
            if (!item.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Object) return GraphPostalAddress.Empty;
            return new GraphPostalAddress(Optional(a, "street", 1024) ?? "", Optional(a, "city", 256) ?? "", Optional(a, "state", 256) ?? "",
                Optional(a, "postalCode", 256) ?? "", Optional(a, "countryOrRegion", 256) ?? "");
        }
        var emails = new List<GraphEmailAddress>();
        if (item.TryGetProperty("emailAddresses", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var e in list.EnumerateArray().Take(3))
                if (e.ValueKind == JsonValueKind.Object && Optional(e, "address", 320) is { Length: > 0 } address)
                    emails.Add(new GraphEmailAddress(address, Optional(e, "name", 512) ?? ""));
        DateOnly? birthday = null;
        if (Optional(item, "birthday", 64) is { } b && DateTimeOffset.TryParse(b, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when))
            birthday = DateOnly.FromDateTime(when.UtcDateTime);
        return new GraphContact
        {
            Id = Required(item, "id", 2048),
            DisplayName = Optional(item, "displayName", 1024) ?? "",
            GivenName = Optional(item, "givenName", 1024) ?? "",
            MiddleName = Optional(item, "middleName", 1024) ?? "",
            Surname = Optional(item, "surname", 1024) ?? "",
            Title = Optional(item, "title", 1024) ?? "",
            Suffix = Optional(item, "generation", 1024) ?? "",
            FileAs = Optional(item, "fileAs", 1024) ?? "",
            CompanyName = Optional(item, "companyName", 1024) ?? "",
            JobTitle = Optional(item, "jobTitle", 1024) ?? "",
            Emails = emails,
            BusinessPhones = Strings(item, "businessPhones"),
            HomePhones = Strings(item, "homePhones"),
            MobilePhone = Optional(item, "mobilePhone", 128) ?? "",
            BusinessAddress = Address("businessAddress"),
            HomeAddress = Address("homeAddress"),
            OtherAddress = Address("otherAddress"),
            BusinessHomePage = Optional(item, "businessHomePage", 2048) ?? "",
            ImAddresses = Strings(item, "imAddresses"),
            PersonalNotes = OptionalText(item, "personalNotes", 65_536) ?? "",
            Birthday = birthday
        };
    }

    private static IReadOnlyList<string> Strings(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array) return [];
        var result = new List<string>();
        foreach (var value in list.EnumerateArray().Take(10))
            if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 256 } text && !text.Any(char.IsControl))
                result.Add(text);
        return result;
    }

    private async Task VerifyAsync(string token, CancellationToken ct)
    {
        if (GraphAccountVerification.IsVerified(expectedAccountId, token)) return;
        using var json = await RequestAsync(HttpMethod.Get, Origin + "/me?$select=id", token, null, ct).ConfigureAwait(false);
        if (Required(json.RootElement, "id", 256) != expectedAccountId)
            throw new GraphMailException("Graph token belongs to a different account.");
        GraphAccountVerification.Mark(expectedAccountId, token);
    }

    private static string CheckedToken(string token) =>
        string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl) ? throw new GraphMailException("A valid access token is required.") : token;

    private static Uri SafeUri(string url)
    {
        var uri = new Uri(url);
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "graph.microsoft.com" || uri.Port != 443 ||
            !uri.AbsolutePath.StartsWith("/v1.0/me/", StringComparison.Ordinal) && uri.AbsolutePath != "/v1.0/me")
            throw new GraphMailException("Graph returned an unsafe contacts URL.");
        return uri;
    }

    private async Task<JsonDocument> RequestAsync(HttpMethod method, string url, string token, object? payload, CancellationToken ct)
    {
        var uri = SafeUri(url);
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CheckedToken(token));
        if (payload is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri != uri || (int)response.StatusCode is >= 300 and < 400)
            throw new GraphMailException("Graph redirected a contacts request.");
        if (!response.IsSuccessStatusCode)
            throw new GraphMailException($"Graph contacts request failed with HTTP {(int)response.StatusCode}.", response.StatusCode);
        if (method == HttpMethod.Post && response.StatusCode != HttpStatusCode.Created)
            throw new GraphMailException("Graph did not confirm that the contact was created.");
        if (method == HttpMethod.Delete)
        {
            if (response.StatusCode != HttpStatusCode.NoContent && response.StatusCode != HttpStatusCode.OK)
                throw new GraphMailException("Graph did not confirm that the contact was deleted.");
            return JsonDocument.Parse("{}");
        }
        if (response.Content.Headers.ContentLength > MaxJsonBytes)
            throw new GraphMailException("Graph contacts response exceeds the size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > MaxJsonBytes) throw new GraphMailException("Graph contacts response exceeds the size limit.");
            buffer.Write(chunk, 0, count);
        }
        buffer.Position = 0;
        try { return await JsonDocument.ParseAsync(buffer, cancellationToken: ct).ConfigureAwait(false); }
        catch (JsonException) { throw new GraphMailException("Graph returned invalid contacts JSON."); }
    }

    private static IReadOnlyList<JsonElement> Value(JsonElement root)
    {
        if (!root.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 100)
            throw new GraphMailException("Graph returned an invalid contacts page.");
        var items = value.EnumerateArray().ToArray();
        if (items.Any(item => item.ValueKind != JsonValueKind.Object))
            throw new GraphMailException("Graph returned invalid contact items.");
        return items;
    }

    private static string? NextLink(JsonElement root)
    {
        var next = Optional(root, "@odata.nextLink", 4096);
        if (next is null) return null;
        if (!Uri.TryCreate(next, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "graph.microsoft.com" ||
            !(uri.AbsolutePath == "/v1.0/me/contacts" || uri.AbsolutePath == "/v1.0/me/contactFolders" ||
              uri.AbsolutePath.StartsWith("/v1.0/me/contactFolders/", StringComparison.Ordinal)))
            throw new GraphMailException("Graph returned an unsafe contacts page link.");
        return next;
    }

    private static string Required(JsonElement root, string name, int limit) =>
        Optional(root, name, limit) ?? throw new GraphMailException("Graph omitted required contact data.");

    private static string? Optional(JsonElement root, string name, int limit)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new GraphMailException("Graph returned invalid contact data.");
        var text = value.GetString();
        if (text is null || text.Length > limit || text.Any(char.IsControl))
            throw new GraphMailException("Graph returned invalid contact data.");
        return text;
    }

    /// <summary>Like Optional, but line breaks and tabs are allowed (notes); other control characters are removed.</summary>
    private static string? OptionalText(JsonElement root, string name, int limit)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new GraphMailException("Graph returned invalid contact data.");
        var text = value.GetString() ?? "";
        if (text.Length > limit) text = text[..limit];
        return new string(text.Where(ch => !char.IsControl(ch) || ch is '\n' or '\r' or '\t').ToArray());
    }

    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 2048 || id.Any(char.IsControl))
            throw new ArgumentException("Invalid contact ID.", nameof(id));
    }
}
