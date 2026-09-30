using System.Net;
using System.Text.Json;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Tests;

public sealed class GraphMailWriterTests
{
    [Fact]
    public async Task CreatesUpdatesAndSendsOnlyTheChosenDraft()
    {
        var calls = new List<(HttpMethod Method, string Path, string? Body)>();
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("graph.microsoft.com", request.RequestUri!.Host);
            Assert.Equal("access", request.Headers.Authorization?.Parameter);
            calls.Add((request.Method, request.RequestUri.AbsolutePath, null));
            var response = request.RequestUri.AbsolutePath switch
            {
                "/v1.0/me" => Json(HttpStatusCode.OK, """{"id":"owner"}"""),
                "/v1.0/me/messages" => Json(HttpStatusCode.Created, """{"id":"draft"}"""),
                "/v1.0/me/messages/draft" => Json(HttpStatusCode.OK, """{"id":"draft"}"""),
                "/v1.0/me/messages/draft/send" => new HttpResponseMessage(HttpStatusCode.Accepted),
                _ => throw new Exception("Unexpected Graph endpoint")
            };
            response.RequestMessage = request;
            return response;
        }));
        var writer = new GraphMailWriter(http, "owner");
        var id = await writer.CreateDraftAsync("access", "a@example.test; b@example.test", "Hello", "Body");
        Assert.Equal("draft", id);
        await writer.UpdateDraftAsync("access", id, "c@example.test", "Edited", "New body");
        await writer.SendDraftAsync("access", id);
        Assert.Equal([HttpMethod.Get, HttpMethod.Post, HttpMethod.Get, HttpMethod.Patch, HttpMethod.Get, HttpMethod.Post],
            calls.Select(call => call.Method));
        Assert.Equal("/v1.0/me/messages/draft/send", calls[^1].Path);
        Assert.Equal("/v1.0/me/messages", calls[1].Path);
    }

    [Theory]
    [InlineData("reply", "createReply")]
    [InlineData("replyAll", "createReplyAll")]
    [InlineData("forward", "createForward")]
    public async Task CreatesThreadedResponseDraft(string kind, string endpoint)
    {
        using var http = new HttpClient(new Handler(request =>
        {
            var response = request.RequestUri!.AbsolutePath switch
            {
                "/v1.0/me" => Json(HttpStatusCode.OK, """{"id":"owner"}"""),
                var path when path == "/v1.0/me/messages/source/" + endpoint =>
                    Json(HttpStatusCode.Created, """{"id":"response-draft"}"""),
                _ => throw new Exception("Unexpected Graph endpoint")
            };
            response.RequestMessage = request;
            return response;
        }));
        Assert.Equal("response-draft", await new GraphMailWriter(http, "owner")
            .CreateResponseDraftAsync("access", "source", kind));
    }

    [Fact]
    public async Task RefusesWrongAccountBeforeAnyMutation()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            calls++;
            Assert.Equal("/v1.0/me", request.RequestUri!.AbsolutePath);
            var response = Json(HttpStatusCode.OK, """{"id":"someone-else"}""");
            response.RequestMessage = request;
            return response;
        }));
        await Assert.ThrowsAsync<GraphMailException>(() =>
            new GraphMailWriter(http, "owner").MoveAsync("access", "source", "deleteditems"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RejectsRedirectWithoutSendingMutation()
    {
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        { Headers = { Location = new Uri("https://untrusted.example/") } }));
        await Assert.ThrowsAsync<GraphMailException>(() =>
            new GraphMailWriter(http, "owner").SetReadAsync("access", "source", true));
    }

    [Fact]
    public async Task ChecksActualTrashFolderBeforePermanentDelete()
    {
        var paths = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            var response = path switch
            {
                "/v1.0/me" => Json(HttpStatusCode.OK, """{"id":"owner"}"""),
                "/v1.0/me/messages/message" => Json(HttpStatusCode.OK,
                    """{"id":"message","parentFolderId":"real-trash-id"}"""),
                "/v1.0/me/mailFolders/deleteditems" => Json(HttpStatusCode.OK, """{"id":"real-trash-id"}"""),
                _ => throw new Exception("Unexpected Graph endpoint")
            };
            response.RequestMessage = request;
            return response;
        }));
        Assert.True(await new GraphMailWriter(http, "owner").IsInDeletedItemsAsync("access", "message"));
        Assert.Equal(["/v1.0/me", "/v1.0/me/messages/message", "/v1.0/me/mailFolders/deleteditems"], paths);
    }

    [Fact]
    public async Task PermanentDeleteUsesTheVerifiedUsersEndpoint()
    {
        var requests = new List<(HttpMethod Method, string Path)>();
        using var http = new HttpClient(new Handler(request =>
        {
            requests.Add((request.Method, request.RequestUri!.AbsolutePath));
            var response = request.RequestUri.AbsolutePath switch
            {
                "/v1.0/me" => Json(HttpStatusCode.OK, """{"id":"owner"}"""),
                "/v1.0/me/messages/message" => Json(HttpStatusCode.OK,
                    """{"id":"message","parentFolderId":"trash-id"}"""),
                "/v1.0/me/mailFolders/deleteditems" => Json(HttpStatusCode.OK, """{"id":"trash-id"}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NoContent)
            };
            response.RequestMessage = request;
            return response;
        }));
        await new GraphMailWriter(http, "owner").DeletePermanentlyAsync("access", "message");
        Assert.Equal((HttpMethod.Post, "/v1.0/users/owner/messages/message/permanentDelete"), requests[^1]);
    }

    [Fact]
    public async Task PermanentDeleteRefusesMessageMovedOutOfTrash()
    {
        var writes = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Post) writes++;
            var response = request.RequestUri!.AbsolutePath switch
            {
                "/v1.0/me" => Json(HttpStatusCode.OK, """{"id":"owner"}"""),
                "/v1.0/me/messages/message" => Json(HttpStatusCode.OK,
                    """{"id":"message","parentFolderId":"inbox-id"}"""),
                "/v1.0/me/mailFolders/deleteditems" => Json(HttpStatusCode.OK, """{"id":"trash-id"}"""),
                _ => throw new Exception("Unexpected endpoint")
            };
            response.RequestMessage = request;
            return response;
        }));
        await Assert.ThrowsAsync<GraphMailException>(() =>
            new GraphMailWriter(http, "owner").DeletePermanentlyAsync("access", "message"));
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task ReadsOriginalHtmlDraftAndUpdatesWithoutLosingQuotedBody()
    {
        string? updatedBody = null;
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Patch)
                updatedBody = await request.Content!.ReadAsStringAsync();
            var response = request.RequestUri!.AbsolutePath switch
            {
                "/v1.0/me" => Json(HttpStatusCode.OK, """{"id":"owner"}"""),
                "/v1.0/me/messages/draft" when request.Method == HttpMethod.Get => Json(HttpStatusCode.OK,
                    """{"id":"draft","isDraft":true,"subject":"Re: News","toRecipients":[{"emailAddress":{"address":"friend@example.test"}}],"body":{"contentType":"html","content":"<blockquote>Original formatted message</blockquote>"}}"""),
                "/v1.0/me/messages/draft" when request.Method == HttpMethod.Patch =>
                    Json(HttpStatusCode.OK, """{"id":"draft"}"""),
                _ => throw new Exception("Unexpected Graph endpoint")
            };
            response.RequestMessage = request;
            return response;
        }));
        var writer = new GraphMailWriter(http, "owner");
        var draft = await writer.GetDraftAsync("access", "draft");
        Assert.Equal("html", draft.ContentType);
        await writer.UpdateDraftAsync("access", "draft", draft.To, draft.Subject,
            "<p>My answer</p>" + draft.Body, "HTML");
        Assert.NotNull(updatedBody);
        using var json = JsonDocument.Parse(updatedBody);
        Assert.Contains("<blockquote>Original formatted message</blockquote>",
            json.RootElement.GetProperty("body").GetProperty("content").GetString());
    }

    [Fact]
    public async Task SavesCcBccAndHtmlInDraftPayload()
    {
        string? payload = null;
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Post) payload = await request.Content!.ReadAsStringAsync();
            var response = request.Method == HttpMethod.Get
                ? Json(HttpStatusCode.OK, """{"id":"owner"}""")
                : Json(HttpStatusCode.Created, """{"id":"draft"}""");
            response.RequestMessage = request;
            return response;
        }));
        await new GraphMailWriter(http, "owner").CreateDraftAsync("access",
            new GraphMailWriter.DraftContent("to@example.test", "cc@example.test", "bcc@example.test",
                "Hello", "<b>Body</b>", "HTML"));
        using var json = JsonDocument.Parse(payload!);
        Assert.Equal("HTML", json.RootElement.GetProperty("body").GetProperty("contentType").GetString());
        Assert.Equal("cc@example.test", json.RootElement.GetProperty("ccRecipients")[0]
            .GetProperty("emailAddress").GetProperty("address").GetString());
        Assert.Equal("bcc@example.test", json.RootElement.GetProperty("bccRecipients")[0]
            .GetProperty("emailAddress").GetProperty("address").GetString());
    }

    [Fact]
    public async Task AddsSmallFileToVerifiedDraft()
    {
        var path = Path.Combine(Path.GetTempPath(), "openoutlook-attach-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(path, "hello");
        try
        {
            string? payload = null;
            using var http = new HttpClient(new Handler(async request =>
            {
                if (request.Method == HttpMethod.Post) payload = await request.Content!.ReadAsStringAsync();
                var response = request.Method == HttpMethod.Get
                    ? Json(HttpStatusCode.OK, """{"id":"owner"}""")
                    : new HttpResponseMessage(HttpStatusCode.Created);
                response.RequestMessage = request;
                return response;
            }));
            await new GraphMailWriter(http, "owner").AddFileAttachmentAsync("access", "draft", path);
            using var json = JsonDocument.Parse(payload!);
            Assert.Equal("#microsoft.graph.fileAttachment", json.RootElement.GetProperty("@odata.type").GetString());
            Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(
                json.RootElement.GetProperty("contentBytes").GetString()!)));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LargeAttachmentUsesPreauthenticatedUploadWithoutBearerToken()
    {
        var path = Path.Combine(Path.GetTempPath(), "openoutlook-large-" + Guid.NewGuid().ToString("N") + ".bin");
        await File.WriteAllBytesAsync(path, new byte[3 * 1024 * 1024 + 1]);
        try
        {
            var ranges = new List<string>();
            using var http = new HttpClient(new Handler(request =>
            {
                HttpResponseMessage response;
                if (request.RequestUri!.Host == "outlook.office.com")
                {
                    Assert.Null(request.Headers.Authorization);
                    ranges.Add(request.Content!.Headers.GetValues("Content-Range").Single());
                    response = ranges.Count == 1
                        ? Json(HttpStatusCode.OK, """{"nextExpectedRanges":["3145728-"]}""")
                        : new HttpResponseMessage(HttpStatusCode.Created);
                }
                else if (request.Method == HttpMethod.Get)
                    response = Json(HttpStatusCode.OK, """{"id":"owner"}""");
                else
                    response = Json(HttpStatusCode.Created,
                        """{"uploadUrl":"https://outlook.office.com/upload?secret=opaque"}""");
                response.RequestMessage = request;
                return response;
            }));
            await new GraphMailWriter(http, "owner").AddFileAttachmentAsync("access", "draft", path);
            Assert.Equal(["bytes 0-3145727/3145729", "bytes 3145728-3145728/3145729"], ranges);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task DraftAttachmentListingSelectsOnlyBaseMetadata()
    {
        var queries = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            HttpResponseMessage response;
            if (request.RequestUri!.AbsolutePath == "/v1.0/me")
                response = Json(HttpStatusCode.OK, """{"id":"owner"}""");
            else
            {
                queries.Add(request.RequestUri.Query);
                Assert.DoesNotContain("contentId", request.RequestUri.Query, StringComparison.OrdinalIgnoreCase);
                response = Json(HttpStatusCode.OK,
                    """{"value":[{"id":"attachment","name":"note.txt","size":42}]}""");
            }
            response.RequestMessage = request;
            return response;
        }));
        var files = await new GraphMailWriter(http, "owner")
            .ListDraftAttachmentsAsync("access", "draft");
        Assert.Single(files);
        Assert.Equal("note.txt", files[0].Name);
        Assert.Single(queries);
    }

    [Fact]
    public async Task ReadsDraftAttachmentFlagWithoutListingFiles()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            calls++;
            var response = request.RequestUri!.AbsolutePath == "/v1.0/me"
                ? Json(HttpStatusCode.OK, """{"id":"owner"}""")
                : Json(HttpStatusCode.OK,
                    """{"id":"draft","isDraft":true,"hasAttachments":false,"subject":"Example","toRecipients":[],"ccRecipients":[],"bccRecipients":[],"body":{"contentType":"html","content":"<p>Hello</p>"}}""");
            response.RequestMessage = request;
            return response;
        }));
        var draft = await new GraphMailWriter(http, "owner").GetDraftAsync("access", "draft");
        Assert.False(draft.HasAttachments);
        Assert.Equal(2, calls);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    { Content = new StringContent(body) };

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond;

        public Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
            _respond = request => Task.FromResult(respond(request));

        public Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => _respond(request);
    }
}
