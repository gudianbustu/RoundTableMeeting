using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Enums;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Repository.Sqlite.Database;
namespace RoundTableMeeting.IntegrationTests;

public sealed class PrivateMessageTests
{
    [Fact]
    public async Task PrivateScopeHidesHistoryReferencesAndCursorFromThirdPersonAndPreservesReplayAndReply()
    {
        using var factory = new TestApplicationFactory();
        using var a = Client(factory); using var b = Client(factory); using var c = Client(factory);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var user = UserAccount.Create("charlie", "Charlie");
            user.InitializePasswordHash(scope.ServiceProvider.GetRequiredService<IPasswordHasher<UserAccount>>().HashPassword(user, "test-password-charlie"));
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>(); db.Add(user); await db.SaveChangesAsync();
        }
        await Login(a, "alice"); await Login(b, "bob"); await Login(c, "charlie");
        var created = await Body(await Post(a, "/api/meetings", new { creationRequestId = Guid.NewGuid(), name = "Private scope" }), HttpStatusCode.Created);
        var id = created.GetProperty("meeting").GetProperty("meetingId").GetGuid();
        var code = created.GetProperty("invitation").GetProperty("meetingCode").GetString();
        var joined = await Body(await Post(b, "/api/meetings/join", new { meetingCode = code, displayName = "Bob" }), HttpStatusCode.OK);
        var peer = joined.GetProperty("currentParticipant").GetProperty("participantId").GetGuid();
        await Body(await Post(c, "/api/meetings/join", new { meetingCode = code, displayName = "Charlie" }), HttpStatusCode.OK);
        var url = $"/api/meetings/{id}/messages";
        await Body(await Post(a, url, new { clientSubmissionId = Guid.NewGuid(), content = "public" }), HttpStatusCode.Created);
        var submission = Guid.NewGuid();
        var body = new { clientSubmissionId = submission, content = "  e\u0301 😀 @Charlie  ", visibility = "private", recipientParticipantId = peer };
        var sent = await Body(await Post(a, url, body), HttpStatusCode.Created);
        var message = sent.GetProperty("message"); var messageId = message.GetProperty("messageId").GetGuid();
        Assert.Equal(body.content, message.GetProperty("content").GetString());
        Assert.Empty(message.GetProperty("mentions").EnumerateArray());
        var replay = await Body(await Post(a, url, body), HttpStatusCode.OK);
        Assert.Equal(messageId, replay.GetProperty("message").GetProperty("messageId").GetGuid());
        Assert.Equal(HttpStatusCode.Conflict, (await Post(a, url, new { clientSubmissionId = submission, content = body.content })).StatusCode);
        var hidden = await c.GetFromJsonAsync<JsonElement>(url);
        Assert.Single(hidden.GetProperty("items").EnumerateArray());
        Assert.Equal(1, hidden.GetProperty("visibleThroughSequence").GetInt64());
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync(url + "/" + messageId)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(c, url, new { clientSubmissionId = Guid.NewGuid(), content = "guess", replyToMessageId = messageId })).StatusCode);
        var reply = await Body(await Post(b, url, new { clientSubmissionId = Guid.NewGuid(), content = "reply", replyToMessageId = messageId }), HttpStatusCode.Created);
        Assert.Equal("private", reply.GetProperty("message").GetProperty("visibility").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await Post(b, url, new { clientSubmissionId = Guid.NewGuid(), content = "expose", visibility = "public", replyToMessageId = messageId })).StatusCode);
        var ownStatus = await a.GetFromJsonAsync<JsonElement>($"/api/meetings/{id}/submissions/{submission}");
        Assert.Equal("saved", ownStatus.GetProperty("status").GetString());
        var foreignStatus = await c.GetFromJsonAsync<JsonElement>($"/api/meetings/{id}/submissions/{submission}");
        Assert.Equal("not_found", foreignStatus.GetProperty("status").GetString());
        await Body(await Post(b, $"/api/meetings/{id}/leave", new { operationId = Guid.NewGuid() }), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(a, url, new { clientSubmissionId = Guid.NewGuid(), content = "left", visibility = "private", recipientParticipantId = peer })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(a, url, body)).StatusCode);
        Assert.Equal(HttpStatusCode.UpgradeRequired, (await a.GetAsync("/mcp")).StatusCode);
    }
    [Fact]
    public async Task LongOriginalTextAndFilteredPaginationRespectLeaveBoundary()
    {
        using var factory = new TestApplicationFactory();
        using var a = Client(factory); using var b = Client(factory);
        await Login(a, "alice"); await Login(b, "bob");
        var created = await Body(await Post(a, "/api/meetings", new { creationRequestId = Guid.NewGuid(), name = "Scoped pages" }), HttpStatusCode.Created);
        var id = created.GetProperty("meeting").GetProperty("meetingId").GetGuid();
        var code = created.GetProperty("invitation").GetProperty("meetingCode").GetString();
        var joined = await Body(await Post(b, "/api/meetings/join", new { meetingCode = code, displayName = "Bob" }), HttpStatusCode.OK);
        var peer = joined.GetProperty("currentParticipant").GetProperty("participantId").GetGuid();
        var url = $"/api/meetings/{id}/messages";
        var content = "  " + string.Concat(Enumerable.Repeat("e\u0301😀", 30000)) + "  ";
        var sent = await Body(await Post(a, url, new { clientSubmissionId = Guid.NewGuid(), content }), HttpStatusCode.Created);
        Assert.Equal(content, sent.GetProperty("message").GetProperty("content").GetString());
        await Body(await Post(a, url, new { clientSubmissionId = Guid.NewGuid(), content = "private gap", visibility = "private", recipientParticipantId = peer }), HttpStatusCode.Created);
        await Body(await Post(a, url, new { clientSubmissionId = Guid.NewGuid(), content = "public last" }), HttpStatusCode.Created);
        var page = await a.GetFromJsonAsync<JsonElement>(url + "?scope=public&afterSequence=0&limit=1");
        Assert.True(page.GetProperty("hasMoreAfter").GetBoolean());
        Assert.Equal(content, page.GetProperty("items")[0].GetProperty("content").GetString());
        var next = await a.GetFromJsonAsync<JsonElement>(url + "?scope=public&afterSequence=1&limit=1");
        Assert.False(next.GetProperty("hasMoreAfter").GetBoolean());
        Assert.Equal(3, next.GetProperty("items")[0].GetProperty("sequence").GetInt64());
        var privatePage = await a.GetFromJsonAsync<JsonElement>(url + $"?scope=private&peerParticipantId={peer}");
        Assert.Single(privatePage.GetProperty("items").EnumerateArray());
        await Body(await Post(b, $"/api/meetings/{id}/leave", new { operationId = Guid.NewGuid() }), HttpStatusCode.OK);
        await Body(await Post(a, url, new { clientSubmissionId = Guid.NewGuid(), content = "after exit" }), HttpStatusCode.Created);
        var history = await b.GetFromJsonAsync<JsonElement>(url);
        Assert.Equal(3, history.GetProperty("items").GetArrayLength());
        Assert.Equal(3, history.GetProperty("visibleThroughSequence").GetInt64());
        Assert.Equal(HttpStatusCode.Conflict, (await Post(b, url, new { clientSubmissionId = Guid.NewGuid(), content = "cannot send" })).StatusCode);
    }
    [Fact]
    public async Task CurrentProtocolStopsTaskDispatchAndPreservesHistoricalPendingDelivery()
    {
        using var factory = new TestApplicationFactory(); using var a = Client(factory); await Login(a, "alice");
        var created = await Body(await Post(a, "/api/meetings", new { creationRequestId = Guid.NewGuid(), name = "Protocol cutover" }), HttpStatusCode.Created);
        var id = created.GetProperty("meeting").GetProperty("meetingId").GetGuid();
        await Body(await Post(a, $"/api/meetings/{id}/messages", new { clientSubmissionId = Guid.NewGuid(), content = "@Alice first" }), HttpStatusCode.Created);
        Guid historicalId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            Assert.Equal(new[] { "realtime-broadcast-v1" }, scope.ServiceProvider.GetRequiredService<IMessageEventConsumerCatalog>().ConsumerNames);
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            var evt = await db.MessageAppendedEvents.SingleAsync(x => x.MeetingId == id);
            var old = MessageEventDelivery.Create(evt.ID, "agent-mention-task-v1", DateTimeOffset.UtcNow.AddDays(-1));
            historicalId = old.ID; db.Add(old); await db.SaveChangesAsync();
        }
        await Body(await Post(a, $"/api/meetings/{id}/messages", new { clientSubmissionId = Guid.NewGuid(), content = "@Alice second" }), HttpStatusCode.Created);
        var completed = false;
        for (var i = 0; i < 100; i++)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            if (await db.MessageEventDeliveries.CountAsync(x => x.ConsumerName == "realtime-broadcast-v1" && x.State == MessageEventDeliveryState.Completed) == 2) { completed = true; break; }
            await Task.Delay(50);
        }
        Assert.True(completed);
        await using var final = factory.Services.CreateAsyncScope();
        var data = final.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        Assert.Equal(MessageEventDeliveryState.Pending, (await data.MessageEventDeliveries.SingleAsync(x => x.ID == historicalId)).State);
        Assert.Empty(await data.AgentReplyTasks.ToListAsync());
        var rejected = await Body(await a.GetAsync("/mcp"), HttpStatusCode.UpgradeRequired);
        Assert.Equal("protocol_upgrade_required", rejected.GetProperty("code").GetString());
    }
    private static HttpClient Client(TestApplicationFactory f) => f.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });
    private static async Task Login(HttpClient c, string name) => await Body(await Post(c, "/api/auth/login", new { username = name, password = "test-password-" + name }), HttpStatusCode.OK);
    private static async Task<HttpResponseMessage> Post(HttpClient c, string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("Origin", "http://localhost:5173"); return await c.SendAsync(request);
    }
    private static async Task<JsonElement> Body(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {expected}, got {response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<JsonElement>(body);
    }
}
