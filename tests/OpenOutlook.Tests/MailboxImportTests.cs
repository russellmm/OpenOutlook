using System.Net;
using System.Text;
using System.Text.Json;
using OpenOutlook.Providers.Google;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Tests;

/// <summary>Filing a finished MIME message in a mailbox (the "copy/move a PST message into a mailbox" path): exact requests against fake servers.</summary>
public sealed class MailboxImportTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = respond(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private static readonly byte[] Mime = Encoding.ASCII.GetBytes("From: a@example.org\r\nTo: b@example.org\r\nSubject: Hi\r\n\r\nbody\r\n");

    // ---- Gmail ---------------------------------------------------------------------------------------------------------------------------

    private static (GmailMailbox Box, List<(string Method, string PathAndQuery, string Body)> Calls) Gmail(HttpStatusCode importStatus = HttpStatusCode.OK)
    {
        var calls = new List<(string, string, string)>();
        var client = new HttpClient(new Handler(request =>
        {
            var body = request.Content is null ? "" : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var path = request.RequestUri!.AbsolutePath.Replace("/gmail/v1/users/me", "");
            if (path == "/profile") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"emailAddress":"me@example.org"}""", Encoding.UTF8, "application/json") };
            calls.Add((request.Method.Method, path + request.RequestUri.Query, body));
            return new HttpResponseMessage(importStatus) { Content = new StringContent("""{"id":"gm-77"}""", Encoding.UTF8, "application/json") };
        }));
        return (new GmailMailbox(client, _ => ValueTask.FromResult("tok"), "me@example.org"), calls);
    }

    [Fact]
    public async Task Gmail_import_posts_the_raw_message_with_its_labels_and_keeps_the_date_header()
    {
        var (box, calls) = Gmail();
        var id = await box.ImportAsync(Mime, ["Label_5"], read: true);
        Assert.Equal("gm-77", id);
        var call = Assert.Single(calls);
        Assert.Equal("POST", call.Method);
        Assert.Equal("/messages/import?internalDateSource=dateHeader&neverMarkSpam=true", call.PathAndQuery);
        var json = JsonDocument.Parse(call.Body).RootElement;
        Assert.Equal(Mime, GmailMimeBuilder.FromBase64Url(json.GetProperty("raw").GetString()!));
        Assert.Equal(["Label_5"], json.GetProperty("labelIds").EnumerateArray().Select(x => x.GetString()).ToArray());
    }

    [Fact]
    public async Task Gmail_import_adds_unread_for_an_unread_message_and_never_twice()
    {
        var (box, calls) = Gmail();
        await box.ImportAsync(Mime, ["INBOX", "UNREAD"], read: false);
        var labels = JsonDocument.Parse(calls[0].Body).RootElement.GetProperty("labelIds").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Equal(["INBOX", "UNREAD"], labels);
        calls.Clear();
        await box.ImportAsync(Mime, ["INBOX", "UNREAD"], read: true);
        Assert.Equal(["INBOX"], JsonDocument.Parse(calls[0].Body).RootElement.GetProperty("labelIds").EnumerateArray().Select(x => x.GetString()).ToArray());
    }

    [Fact]
    public async Task Gmail_import_refuses_nothing_to_import_bad_labels_and_explains_a_refused_scope()
    {
        var (box, _) = Gmail();
        await Assert.ThrowsAsync<ArgumentException>(() => box.ImportAsync([], ["INBOX"], true));
        await Assert.ThrowsAsync<ArgumentException>(() => box.ImportAsync(Mime, ["bad label\n"], true));
        var (denied, _) = Gmail(HttpStatusCode.Forbidden);
        var error = await Assert.ThrowsAsync<GmailReadException>(() => denied.ImportAsync(Mime, ["INBOX"], true));
        Assert.Contains("organizing mail", error.Message);
        Assert.DoesNotContain("sending", error.Message);
    }

    // ---- Microsoft Graph -----------------------------------------------------------------------------------------------------------------

    private static (GraphMailWriter Writer, List<(string Method, string Path, string ContentType, string Body)> Calls) Graph()
    {
        var calls = new List<(string, string, string, string)>();
        var http = new HttpClient(new Handler(request =>
        {
            var body = request.Content is null ? "" : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1.0/me") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"id":"owner"}""") };
            calls.Add((request.Method.Method, path, request.Content?.Headers.ContentType?.MediaType ?? "", body));
            return request.Method == HttpMethod.Post
                ? new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("""{"id":"graph-new","isDraft":false}""") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"id":"graph-new"}""") };
        }));
        return (new GraphMailWriter(http, "owner"), calls);
    }

    [Fact]
    public async Task Graph_import_posts_base64_mime_as_text_plain_into_the_folder_and_marks_it_read()
    {
        var (writer, calls) = Graph();
        var id = await writer.ImportMimeAsync("access", "folder A/1", Mime, read: true);
        Assert.Equal("graph-new", id);
        Assert.Equal(2, calls.Count);
        Assert.Equal(("POST", "/v1.0/me/mailFolders/folder%20A%2F1/messages", "text/plain"), (calls[0].Method, calls[0].Path.Replace("folder A/1", "folder%20A%2F1"), calls[0].ContentType));
        Assert.Equal(Mime, Convert.FromBase64String(calls[0].Body));
        Assert.Equal(("PATCH", "/v1.0/me/messages/graph-new"), (calls[1].Method, calls[1].Path));
        Assert.True(JsonDocument.Parse(calls[1].Body).RootElement.GetProperty("isRead").GetBoolean());
    }

    [Fact]
    public async Task Graph_import_leaves_an_unread_message_unread_and_validates_its_input()
    {
        var (writer, calls) = Graph();
        await writer.ImportMimeAsync("access", "inbox", Mime, read: false);
        Assert.Single(calls);
        await Assert.ThrowsAsync<ArgumentException>(() => writer.ImportMimeAsync("access", "inbox", [], false));
        await Assert.ThrowsAsync<ArgumentException>(() => writer.ImportMimeAsync("access", "", Mime, false));
        await Assert.ThrowsAsync<ArgumentException>(() => writer.ImportMimeAsync("access", "inbox", new byte[36 * 1024 * 1024], false));
    }
}
