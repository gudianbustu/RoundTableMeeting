using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RoundTableMeeting.Abstraction.Services;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
namespace RoundTableMeeting.IntegrationTests;

public sealed class CurrentMcpTests
{
    internal static async Task<(string Session, JsonElement Result)> Initialize(HttpClient client)
    {
        using var response = await Rpc(client, null, "initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "current-mcp-tests", version = "2" } });
        var body = await Json(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (response.Headers.GetValues("Mcp-Session-Id").Single(), body.GetProperty("result").Clone());
    }
    internal static async Task<JsonElement> Call(HttpClient client, string session, string name, object arguments, bool success = true)
    {
        using var response = await Rpc(client, session, "tools/call", new { name, arguments });
        var rpc = await Json(response);
        Assert.True(rpc.TryGetProperty("result", out var result), rpc.ToString());
        Assert.True(result.TryGetProperty("structuredContent", out var body), result.ToString());
        Assert.True(success == body.GetProperty("succeeded").GetBoolean(), body.ToString());
        return success ? body.GetProperty("data").Clone() : body.Clone();
    }
    private static async Task<HttpResponseMessage> Rpc(HttpClient c, string? session, string method, object args)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(new { jsonrpc = "2.0", id = Guid.NewGuid().ToString(), method, @params = args }) };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        if (session is not null) request.Headers.Add("Mcp-Session-Id", session);
        return await c.SendAsync(request);
    }
    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        var json = text.TrimStart().StartsWith('{') ? text : text.Split('\n').First(l => l.StartsWith("data:", StringComparison.Ordinal))[5..].Trim();
        return JsonDocument.Parse(json).RootElement.Clone();
    }
    private static async Task<JsonElement> Post(HttpClient c, string url, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("Origin", "http://localhost:5173");
        using var response = await c.SendAsync(request); var result = await Json(response);
        Assert.True(response.IsSuccessStatusCode, result.ToString()); return result;
    }
    private static HttpClient Client(TestApplicationFactory f) => f.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });
    [Fact]
    public async Task ToolsExposeCurrentConcreteSchemasAndNoTaskProtocol()
    {
        using var f = new TestApplicationFactory(); using var c = Client(f);
        var init = await Initialize(c); Assert.Equal("mcp-preview-v2", init.Result.GetProperty("serverInfo").GetProperty("version").GetString());
        using var response = await Rpc(c, init.Session, "tools/list", new { });
        var tools = (await Json(response)).GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
        Assert.Contains(tools, t => t.GetProperty("name").GetString() == "wait_messages");
        Assert.DoesNotContain(tools, t => t.GetProperty("name").GetString() is "wait_for_mention" or "reply_to_mention" or "confirm_context");
        Assert.All(tools, t => Assert.True(t.TryGetProperty("outputSchema", out _), t.ToString()));
    }
    [Fact]
    public async Task JoinPrivateVisibilityReplayFragmentWaitTakeoverFilesAndLeave()
    {
        using var f = new TestApplicationFactory(); using var human = Client(f); using var c = Client(f);
        await Post(human, "/api/auth/login", new { username = "alice", password = "test-password-alice" });
        var created = await Post(human, "/api/meetings", new { creationRequestId = Guid.NewGuid(), name = "MCP preview" });
        var meetingId = created.GetProperty("meeting").GetProperty("meetingId").GetGuid();
        var code = created.GetProperty("invitation").GetProperty("meetingCode").GetString();
        var workspace = await human.GetFromJsonAsync<JsonElement>($"/api/meetings/{meetingId}");
        var humanId = workspace.GetProperty("currentParticipant").GetProperty("participantId").GetGuid();
        var a = await Initialize(c); var b = await Initialize(c);
        var ja = await Call(c, a.Session, "join_meeting", new { meetingCode = code, displayName = "AgentA" });
        var jb = await Call(c, b.Session, "join_meeting", new { meetingCode = code, displayName = "AgentB" });
        var aid = ja.GetProperty("connectionSessionId").GetGuid(); var ap = ja.GetProperty("participantId").GetGuid();
        var bid = jb.GetProperty("connectionSessionId").GetGuid();
        var repeated = await Call(c, a.Session, "join_meeting", new { meetingCode = code, displayName = "AgentA" });
        Assert.Equal(aid, repeated.GetProperty("connectionSessionId").GetGuid());
        var humanPrivate = await Post(human, $"/api/meetings/{meetingId}/messages", new { clientSubmissionId = Guid.NewGuid(), content = "secret😀", visibility = "private", recipientParticipantId = ap });
        var hiddenId = humanPrivate.GetProperty("message").GetProperty("messageId").GetGuid();
        var hidden = await Call(c, b.Session, "read_messages", new { connectionSessionId = bid });
        Assert.Empty(hidden.GetProperty("items").EnumerateArray());
        Assert.Equal(0, hidden.GetProperty("visibleThroughSequence").GetInt64());
        var denied = await Call(c, b.Session, "get_message", new { connectionSessionId = bid, messageId = hiddenId }, false);
        Assert.Equal("message_not_found", denied.GetProperty("errorCode").GetString());
        var first = await Call(c, a.Session, "read_messages", new { connectionSessionId = aid, characterBudget = 6 });
        Assert.Equal("secret", first.GetProperty("items")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.Equal(0, first.GetProperty("nextCursor").GetProperty("afterSequence").GetInt64());
        var last = await Call(c, a.Session, "read_messages", new { connectionSessionId = aid, contentOffset = 6, characterBudget = 1 });
        Assert.Equal("😀", last.GetProperty("items")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.True(last.GetProperty("items")[0].GetProperty("complete").GetBoolean());
        var submission = Guid.NewGuid();
        var payload = new { connectionSessionId = aid, request = new { clientSubmissionId = submission, content = "private reply", replyToMessageId = hiddenId } };
        var sent = await Call(c, a.Session, "send_message", payload);
        Assert.Equal("private", sent.GetProperty("visibility").GetString());
        Assert.Equal("agent", sent.GetProperty("author").GetProperty("kind").GetString());
        Assert.Equal(humanId, sent.GetProperty("recipient").GetProperty("participantId").GetGuid());
        var replay = await Call(c, a.Session, "send_message", payload);
        Assert.Equal(sent.GetProperty("messageId").GetGuid(), replay.GetProperty("messageId").GetGuid());
        var mismatch = await Call(c, a.Session, "send_message", new { connectionSessionId = aid, request = new { clientSubmissionId = submission, content = "changed" } }, false);
        Assert.Equal("submission_mismatch", mismatch.GetProperty("errorCode").GetString());
        var status = await Call(c, a.Session, "get_submission", new { connectionSessionId = aid, clientSubmissionId = submission });
        Assert.Equal("saved", status.GetProperty("status").GetString());
        var renewed = await Call(c, a.Session, "renew_agent_lease", new { connectionSessionId = aid });
        Assert.Equal(JsonValueKind.Null, renewed.GetProperty("lifecycleProof").ValueKind);
        var wait = await Call(c, a.Session, "wait_messages", new { connectionSessionId = aid, afterSequence = 2, timeoutSeconds = 1 });
        Assert.Equal("idle", wait.GetProperty("kind").GetString());
        var ticket = await Call(c, a.Session, "prepare_attachment_transfer", new { connectionSessionId = aid, direction = "upload" });
        var uploadId = Guid.NewGuid();
        using var upload = new HttpRequestMessage(HttpMethod.Post, $"/mcp-files?uploadRequestId={uploadId}&name=hello.txt") { Content = new StringContent("hello attachment") };
        upload.Headers.Authorization = new("Bearer", ticket.GetProperty("bearerToken").GetString());
        using var uploaded = await c.SendAsync(upload); Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        var fileId = (await Json(uploaded)).GetProperty("attachmentId").GetGuid();
        var withFile = await Call(c, a.Session, "send_message", new { connectionSessionId = aid, request = new { clientSubmissionId = Guid.NewGuid(), content = "file", visibility = "private", recipientParticipantId = humanId, attachmentIds = new[] { fileId } } });
        Assert.Single(withFile.GetProperty("attachments").EnumerateArray());
        var ownDownload = await Call(c, a.Session, "prepare_attachment_transfer", new { connectionSessionId = aid, direction = "download", attachmentId = fileId });
        using var ownRequest = new HttpRequestMessage(HttpMethod.Get, "/mcp-files"); ownRequest.Headers.Authorization = new("Bearer", ownDownload.GetProperty("bearerToken").GetString());
        using var ownResponse = await c.SendAsync(ownRequest); Assert.Equal(HttpStatusCode.OK, ownResponse.StatusCode);
        Assert.Equal("hello attachment", await ownResponse.Content.ReadAsStringAsync());
        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, "/mcp-files"); rangeRequest.Headers.Authorization = new("Bearer", ownDownload.GetProperty("bearerToken").GetString());
        rangeRequest.Headers.Range = new(0, 4);
        using var rangeResponse = await c.SendAsync(rangeRequest); Assert.Equal(HttpStatusCode.PartialContent, rangeResponse.StatusCode);
        Assert.Equal("hello", await rangeResponse.Content.ReadAsStringAsync());        var downloadTicket = await Call(c, b.Session, "prepare_attachment_transfer", new { connectionSessionId = bid, direction = "download", attachmentId = fileId });
        using var download = new HttpRequestMessage(HttpMethod.Get, "/mcp-files"); download.Headers.Authorization = new("Bearer", downloadTicket.GetProperty("bearerToken").GetString());
        using var unavailable = await c.SendAsync(download); Assert.Equal(HttpStatusCode.NotFound, unavailable.StatusCode);
        var next = await Initialize(c);
        var takeover = await Call(c, next.Session, "join_meeting", new { meetingCode = code, displayName = "AgentA", joinMode = "takeover" });
        Assert.Equal(ap, takeover.GetProperty("participantId").GetGuid());
        await Call(c, a.Session, "send_message", new { connectionSessionId = aid, request = new { clientSubmissionId = Guid.NewGuid(), content = "stale" } }, false);
        using var staleUpload = new HttpRequestMessage(HttpMethod.Post, $"/mcp-files?uploadRequestId={Guid.NewGuid()}&name=x") { Content = new StringContent("bad") };
        staleUpload.Headers.Authorization = new("Bearer", ticket.GetProperty("bearerToken").GetString());
        using var stale = await c.SendAsync(staleUpload); Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        var newId = takeover.GetProperty("connectionSessionId").GetGuid();
        await Call(c, next.Session, "disconnect_agent", new { connectionSessionId = newId });
        var unavailableJoin = await Call(c, next.Session, "join_meeting", new { meetingCode = code, displayName = "AgentA", joinMode = "reconnect" }, false);
        Assert.Equal("new_transport_required", unavailableJoin.GetProperty("errorCode").GetString());
        next = await Initialize(c);
        var reconnected = await Call(c, next.Session, "join_meeting", new { meetingCode = code, displayName = "AgentA", joinMode = "reconnect" });
        Assert.Equal(ap, reconnected.GetProperty("participantId").GetGuid()); newId = reconnected.GetProperty("connectionSessionId").GetGuid();        var left = await Call(c, next.Session, "leave_meeting", new { connectionSessionId = newId, operationId = Guid.NewGuid() });
        Assert.Equal("left", left.GetProperty("membershipState").GetString());
        var roster = await human.GetFromJsonAsync<JsonElement>($"/api/meetings/{meetingId}");
        Assert.Equal("left", roster.GetProperty("participants").EnumerateArray().Single(p => p.GetProperty("participantId").GetGuid() == ap).GetProperty("membershipState").GetString());
        var info = await Call(c, b.Session, "get_meeting_info", new { connectionSessionId = bid });
        Assert.Equal(humanId, info.GetProperty("participants").EnumerateArray().Single(p => p.GetProperty("isCreator").GetBoolean()).GetProperty("participantId").GetGuid());
        await Call(c, next.Session, "read_messages", new { connectionSessionId = newId }, false);
    }
    [Fact]
    public async Task WaitSkipsUnaddressedPublicMessagesAndStopsWhenMeetingIsDeleted()
    {
        using var f = new TestApplicationFactory(); using var human = Client(f); using var c = Client(f);
        await Post(human, "/api/auth/login", new { username = "alice", password = "test-password-alice" });
        var created = await Post(human, "/api/meetings", new { creationRequestId = Guid.NewGuid(), name = "MCP wait" });
        var id = created.GetProperty("meeting").GetProperty("meetingId").GetGuid();
        var code = created.GetProperty("invitation").GetProperty("meetingCode").GetString();
        var session = await Initialize(c);
        var joined = await Call(c, session.Session, "join_meeting", new { meetingCode = code, displayName = "AgentA" });
        var connection = joined.GetProperty("connectionSessionId").GetGuid(); var participant = joined.GetProperty("participantId").GetGuid();
        await Post(human, $"/api/meetings/{id}/messages", new { clientSubmissionId = Guid.NewGuid(), content = "ordinary" });
        var waiting = Call(c, session.Session, "wait_messages", new { connectionSessionId = connection, timeoutSeconds = 5 });
        await Post(human, $"/api/meetings/{id}/messages", new { clientSubmissionId = Guid.NewGuid(), content = "@AgentA inspect", mentionCandidates = new[] { new { participantId = participant, startOffset = 0, length = 7 } } });
        var result = await waiting.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Single(result.GetProperty("items").EnumerateArray()); Assert.Equal(2, result.GetProperty("nextCursor").GetProperty("afterSequence").GetInt64());
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var activity = await scope.ServiceProvider.GetRequiredService<IAgentActivityService>().FindAsync(id, participant);
            Assert.Equal("waiting", activity!.State); Assert.Null(activity.ActiveTaskId);
            var online = await scope.ServiceProvider.GetRequiredService<RoundTableMeeting.AgentCollaboration.CurrentMcpSessionService>().GetOnlineAsync(id, CancellationToken.None);
            Assert.Contains(participant, online);
        }
        var deletingWait = Call(c, session.Session, "wait_messages", new { connectionSessionId = connection, afterSequence = 2, timeoutSeconds = 5 }, false);
        await Post(human, $"/api/meetings/{id}/deletion", new { confirmationName = "MCP wait" });
        await deletingWait.WaitAsync(TimeSpan.FromSeconds(8));
        JsonElement status = default;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            status = await Call(c, session.Session, "get_meeting_status", new { meetingId = id, lifecycleProof = joined.GetProperty("lifecycleProof").GetString() });
            if (status.GetProperty("status").GetString() == "deleted") break;
            await Task.Delay(50);
        }
        Assert.Equal("deleted", status.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ExpiredLeaseRequiresNewTransportAndKeepsParticipantIdentity()
    {
        using var original = new TestApplicationFactory(); var clock = new ManualTimeProvider();
        using var f = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock); }));
        using var human = f.CreateClient(); using var c = f.CreateClient();
        await Post(human, "/api/auth/login", new { username = "alice", password = "test-password-alice" });
        var created = await Post(human, "/api/meetings", new { creationRequestId = Guid.NewGuid(), name = "MCP expiry" });
        var code = created.GetProperty("invitation").GetProperty("meetingCode").GetString();
        var session = await Initialize(c); var next = await Initialize(c);
        var joined = await Call(c, session.Session, "join_meeting", new { meetingCode = code, displayName = "Expiring" });
        await Call(c, next.Session, "join_meeting", new { meetingCode = code, displayName = "Expiring" }, false);
        var online = await Call(c, next.Session, "join_meeting", new { meetingCode = code, displayName = "Expiring", joinMode = "reconnect" }, false);
        Assert.Equal("agent_online_use_takeover", online.GetProperty("errorCode").GetString());
        clock.Advance(TimeSpan.FromMinutes(6));
        var expired = await Call(c, session.Session, "renew_agent_lease", new { connectionSessionId = joined.GetProperty("connectionSessionId").GetGuid() }, false);
        Assert.Equal("connection_expired_reconnect", expired.GetProperty("errorCode").GetString());
        var recovered = await Call(c, next.Session, "join_meeting", new { meetingCode = code, displayName = "Expiring", joinMode = "reconnect" });
        Assert.Equal(joined.GetProperty("participantId").GetGuid(), recovered.GetProperty("participantId").GetGuid());
        Assert.Equal(2, recovered.GetProperty("connectionGeneration").GetInt64());
    }}
