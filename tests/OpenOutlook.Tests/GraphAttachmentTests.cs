using System.Net;
using OpenOutlook.Desktop;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Tests;

public sealed class GraphAttachmentTests
{
    [Fact]
    public async Task ListsTypesAndStreamsFileOnlyForVerifiedAccount()
    {
        var requests = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("fake-token", request.Headers.Authorization?.Parameter);
            requests.Add(request.RequestUri!.AbsolutePath);
            return request.RequestUri.AbsolutePath switch
            {
                "/v1.0/me" => Json("""{"id":"account-id"}"""),
                "/v1.0/me/messages/message-id/attachments" => Json("""{"value":[{"@odata.type":"#microsoft.graph.fileAttachment","id":"file-id","name":"photo.jpg","size":4,"isInline":true,"contentId":"photo-1"},{"@odata.type":"#microsoft.graph.referenceAttachment","id":"link-id","name":"shared.url","size":0}]}"""),
                "/v1.0/me/messages/message-id/attachments/file-id/$value" => new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent([1, 2, 3, 4]) },
                _ => throw new Exception("Unexpected Graph request")
            };
        }));
        var reader = new GraphAttachmentReader(http, "account-id");
        var attachments = await reader.ListAsync("fake-token", "message-id");
        Assert.Equal(2, attachments.Count);
        Assert.Equal(GraphAttachmentKind.File, attachments[0].Kind);
        Assert.Equal("photo-1", attachments[0].ContentId);
        Assert.Equal(GraphAttachmentKind.Reference, attachments[1].Kind);
        await Assert.ThrowsAsync<NotSupportedException>(() => reader.CopyFileAsync("fake-token", "message-id",
            attachments[1], new MemoryStream()));
        await using var output = new MemoryStream();
        Assert.Equal(4, await reader.CopyFileAsync("fake-token", "message-id", attachments[0], output));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, output.ToArray());
        Assert.Equal(2, requests.Count(path => path == "/v1.0/me"));
    }

    [Fact]
    public async Task WrongAccountStopsBeforeAttachmentRequest()
    {
        var count = 0;
        using var http = new HttpClient(new Handler(_ => { count++; return Json("""{"id":"other"}"""); }));
        await Assert.ThrowsAsync<GraphMailException>(() => new GraphAttachmentReader(http, "account-id")
            .ListAsync("fake-token", "message-id"));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task UnsafeNextPageAndRedirectAreRejected()
    {
        using var http = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath == "/v1.0/me"
            ? Json("""{"id":"account-id"}""")
            : Json("""{"value":[],"@odata.nextLink":"https://untrusted.example/steal"}""")));
        await Assert.ThrowsAsync<GraphMailException>(() => new GraphAttachmentReader(http, "account-id")
            .ListAsync("fake-token", "message-id"));
        using var redirected = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)));
        await Assert.ThrowsAsync<GraphMailException>(() => new GraphAttachmentReader(redirected, "account-id")
            .ListAsync("fake-token", "message-id"));
    }

    [Fact]
    public async Task ExportIsPrivateAndNeverOverwritesOrLeavesPartialFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "graph-attachment-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var attachment = new GraphAttachment("id", "photo.jpg", 4, false, GraphAttachmentKind.File);
            var destination = Path.Combine(directory, attachment.Name);
            await GraphAttachmentExporter.ExportAsync(attachment, destination, async (stream, ct) =>
            { await stream.WriteAsync(new byte[] { 1, 2, 3, 4 }, ct); return 4; });
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(destination));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(destination) & (UnixFileMode)0x1FF);
            await Assert.ThrowsAsync<IOException>(() => GraphAttachmentExporter.ExportAsync(attachment,
                destination, (_, _) => throw new Exception("Must not copy")));
            Assert.Single(Directory.GetFiles(directory));
            var second = Path.Combine(directory, "second.jpg");
            await Assert.ThrowsAsync<IOException>(() => GraphAttachmentExporter.ExportAsync(attachment,
                second, async (stream, ct) =>
                {
                    await stream.WriteAsync(new byte[] { 1 }, ct);
                    throw new IOException("Interrupted download");
                }));
            Assert.False(File.Exists(second));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = respond(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
