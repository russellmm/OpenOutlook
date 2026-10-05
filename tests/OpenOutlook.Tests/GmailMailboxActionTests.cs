using System.Net;
using System.Text;
using System.Text.Json;
using OpenOutlook.Auth;
using OpenOutlook.Providers.Google;

namespace OpenOutlook.Tests;

public sealed class GmailMailboxActionTests
{
    private sealed record Call(string Method, string Path, string Body);

    private static (GmailMailbox Box, List<Call> Calls) Make(Func<Call, HttpStatusCode>? status = null)
    {
        var calls = new List<Call>();
        var client = new HttpClient(new Handler(request =>
        {
            var body = request.Content is null ? "" : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var call = new Call(request.Method.Method, request.RequestUri!.AbsolutePath.Replace("/gmail/v1/users/me", ""), body);
            if (call.Path != "/profile") calls.Add(call);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            var code = call.Path == "/profile" ? HttpStatusCode.OK : status?.Invoke(call) ?? HttpStatusCode.OK;
            return new HttpResponseMessage(code) { Content = new StringContent(call.Path == "/profile" ? """{"emailAddress":"me@example.org"}""" : "{}", Encoding.UTF8, "application/json") };
        }));
        return (new GmailMailbox(client, _ => ValueTask.FromResult("tok"), "me@example.org"), calls);
    }

    private static JsonElement Json(string body) => JsonDocument.Parse(body).RootElement;
    private static string[] Strings(JsonElement e) => e.EnumerateArray().Select(x => x.GetString()!).ToArray();

    [Fact]
    public async Task One_message_uses_modify_and_several_use_one_batch_request()
    {
        var (box, calls) = Make();
        await box.SetReadAsync(["m1"], true);
        var single = Assert.Single(calls);
        Assert.Equal(("POST", "/messages/m1/modify"), (single.Method, single.Path));
        Assert.Equal(["UNREAD"], Strings(Json(single.Body).GetProperty("removeLabelIds")));
        Assert.Empty(Strings(Json(single.Body).GetProperty("addLabelIds")));

        calls.Clear();
        await box.SetReadAsync(["m1", "m2", "m3"], false);
        var batch = Assert.Single(calls);
        Assert.Equal("/messages/batchModify", batch.Path);
        Assert.Equal(["m1", "m2", "m3"], Strings(Json(batch.Body).GetProperty("ids")));
        Assert.Equal(["UNREAD"], Strings(Json(batch.Body).GetProperty("addLabelIds")));
    }

    [Fact]
    public async Task Star_archive_move_and_trash_send_the_right_labels()
    {
        var (box, calls) = Make();
        await box.SetStarredAsync(["a"], true);
        Assert.Equal(["STARRED"], Strings(Json(calls[^1].Body).GetProperty("addLabelIds")));
        await box.SetStarredAsync(["a"], false);
        Assert.Equal(["STARRED"], Strings(Json(calls[^1].Body).GetProperty("removeLabelIds")));
        await box.ArchiveAsync(["a"]);
        Assert.Equal(["INBOX"], Strings(Json(calls[^1].Body).GetProperty("removeLabelIds")));
        await box.ModifyLabelsAsync(["a"], ["Label_7"], ["INBOX"]);          // a "move"
        Assert.Equal(["Label_7"], Strings(Json(calls[^1].Body).GetProperty("addLabelIds")));
        Assert.Equal(["INBOX"], Strings(Json(calls[^1].Body).GetProperty("removeLabelIds")));
        calls.Clear();
        await box.TrashAsync(["a", "b"]);
        Assert.Equal(["/messages/a/trash", "/messages/b/trash"], calls.Select(c => c.Path));
        Assert.All(calls, c => Assert.Equal("POST", c.Method));
    }

    [Fact]
    public async Task Large_batches_are_split_and_invalid_input_is_refused_before_any_request()
    {
        var (box, calls) = Make();
        await box.ArchiveAsync(Enumerable.Range(0, 2300).Select(i => "m" + i).ToArray());
        Assert.Equal(3, calls.Count);
        Assert.All(calls, c => Assert.Equal("/messages/batchModify", c.Path));
        Assert.Equal([1000, 1000, 300], calls.Select(c => Json(c.Body).GetProperty("ids").GetArrayLength()));
        calls.Clear();
        await Assert.ThrowsAsync<ArgumentException>(() => box.ArchiveAsync(["../evil"]));
        await Assert.ThrowsAsync<ArgumentException>(() => box.ModifyLabelsAsync(["ok"], ["bad?label"], []));
        await Assert.ThrowsAsync<ArgumentException>(() => box.TrashAsync(["a b"]));
        Assert.Empty(calls);
    }

    [Fact]
    public async Task A_refused_change_explains_that_the_account_must_sign_in_again()
    {
        var (box, _) = Make(_ => HttpStatusCode.Forbidden);
        var error = await Assert.ThrowsAsync<GmailReadException>(() => box.SetReadAsync(["m1"], true));
        Assert.Contains("Sign in again", error.Message);
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
    }

    [Fact]
    public async Task Creates_a_label_and_refuses_bad_names()
    {
        string? posted = null;
        var client = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/profile", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"emailAddress":"me@example.org"}""") };
            posted = request.Method + " " + request.RequestUri.AbsolutePath + " " + request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"id":"Label_9","name":"Travel/2026"}""") };
        }));
        var box = new GmailMailbox(client, _ => ValueTask.FromResult("tok"), "me@example.org");
        var label = await box.CreateLabelAsync("Travel/2026");
        Assert.Equal(new GmailLabel("Label_9", "Travel/2026", false, 0, 0), label);
        Assert.StartsWith("POST /gmail/v1/users/me/labels ", posted);
        Assert.Contains("\"name\":\"Travel/2026\"", posted);
        foreach (var bad in new[] { "", "  ", "/x", "x/", "a//b", "bad\nname", new string('a', 300) })
            await Assert.ThrowsAsync<ArgumentException>(() => box.CreateLabelAsync(bad));
    }

    [Fact]
    public void Only_accounts_connected_with_the_modify_scope_may_change_mail()
    {
        ConnectedAccount Acc(OAuthProvider p, params string[]? scopes) => new(p, "id", "a@b.test", "c", DateTimeOffset.UtcNow, scopes);
        Assert.False(Acc(OAuthProvider.Google, "https://www.googleapis.com/auth/gmail.readonly").CanModifyGmail);
        Assert.False(Acc(OAuthProvider.Google).CanModifyGmail);
        Assert.True(Acc(OAuthProvider.Google, "https://www.googleapis.com/auth/gmail.modify").CanModifyGmail);
        Assert.True(Acc(OAuthProvider.Google, "https://mail.google.com/").CanModifyGmail);
        Assert.False(Acc(OAuthProvider.MicrosoftConsumers, "https://www.googleapis.com/auth/gmail.modify").CanModifyGmail);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
