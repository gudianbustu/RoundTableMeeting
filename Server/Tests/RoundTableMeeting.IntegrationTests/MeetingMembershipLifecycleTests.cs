using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoundTableMeeting.Abstraction.Contracts.Meetings;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed class MeetingMembershipLifecycleTests
{
    [Fact]
    public async Task LeaveBoundsEveryHistoryCursorAndExplicitRejoinKeepsIdentity()
    {
        using var factory = new TestApplicationFactory();
        using var owner = Client(factory);
        using var member = Client(factory);
        await Login(owner, "alice"); await Login(member, "bob");
        var created = await Create(owner, "Membership");
        var id = created.GetProperty("meeting").GetProperty("meetingId").GetGuid();
        var code = created.GetProperty("invitation").GetProperty("meetingCode").GetString();
        var joined = await Body(await Post(member, "/api/meetings/join", new { meetingCode = code, displayName = "Member" }), HttpStatusCode.OK);
        var participantId = joined.GetProperty("currentParticipant").GetProperty("participantId").GetGuid();
        await Message(owner, id, "one"); await Message(member, id, "two");
        var operationId = Guid.NewGuid();
        var left = await Body(await Post(member, $"/api/meetings/{id}/leave", new { operationId }), HttpStatusCode.OK);
        Assert.Equal(2, left.GetProperty("visibleThroughSequence").GetInt64());
        await Message(owner, id, "after leave");
        var repeated = await Body(await Post(member, $"/api/meetings/{id}/leave", new { operationId = Guid.NewGuid() }), HttpStatusCode.OK);
        Assert.Equal(2, repeated.GetProperty("visibleThroughSequence").GetInt64());
        foreach (var query in new[] { "", "?beforeSequence=999", "?afterSequence=0&throughSequence=999", "?limit=1" })
        {
            var page = await member.GetFromJsonAsync<JsonElement>($"/api/meetings/{id}/messages{query}", cancellationToken: TestContext.Current.CancellationToken);
            Assert.All(page.GetProperty("items").EnumerateArray(), message => Assert.True(message.GetProperty("sequence").GetInt64() <= 2));
        }
        Assert.Empty((await member.GetFromJsonAsync<JsonElement[]>("/api/meetings", cancellationToken: TestContext.Current.CancellationToken))!);
        Assert.Single((await member.GetFromJsonAsync<JsonElement[]>("/api/meetings?view=history", cancellationToken: TestContext.Current.CancellationToken))!);
        var denied = await Body(await Post(member, $"/api/meetings/{id}/messages", new { clientSubmissionId = Guid.NewGuid(), content = "denied" }), HttpStatusCode.Conflict);
        Assert.Equal("membership_left", denied.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await Post(member, $"/api/meetings/{id}/invitation", null)).StatusCode);
        var creatorLeave = await Body(await Post(owner, $"/api/meetings/{id}/leave", new { operationId = Guid.NewGuid() }), HttpStatusCode.Conflict);
        Assert.Equal("creator_cannot_leave", creatorLeave.GetProperty("code").GetString());
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            var person = await db.MeetingParticipants.SingleAsync(p => p.ID == participantId, cancellationToken: TestContext.Current.CancellationToken);
            var access = await scope.ServiceProvider.GetRequiredService<IMeetingChatSubscriptionService>().AuthorizeAsync(id, person.UserId!.Value, TestContext.Current.CancellationToken);
            Assert.Equal(MessageOperationStatus.MembershipLeft, access.Status);
        }
        var rejoined = await Body(await Post(member, "/api/meetings/join", new { meetingCode = code, displayName = "Ignored rename" }), HttpStatusCode.OK);
        Assert.Equal(participantId, rejoined.GetProperty("currentParticipant").GetProperty("participantId").GetGuid());
        Assert.Equal("Member", rejoined.GetProperty("currentParticipant").GetProperty("displayName").GetString());
        Assert.Equal("joined", rejoined.GetProperty("membershipState").GetString());
        var all = await member.GetFromJsonAsync<JsonElement>($"/api/meetings/{id}/messages", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(3, all.GetProperty("items").GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await Post(member, $"/api/meetings/{id}/leave", new { operationId })).StatusCode);
        // Replaying the earlier leave intent after rejoin must not leave again.
        await Message(member, id, "still joined");
        Assert.Single((await member.GetFromJsonAsync<JsonElement[]>("/api/meetings", cancellationToken: TestContext.Current.CancellationToken))!);
    }

    [Fact]
    public async Task HistoryActionsOnlyAffectTheCurrentMembership()
    {
        using var factory = new TestApplicationFactory();
        using var owner = Client(factory);
        using var member = Client(factory);
        await Login(owner, "alice");
        await Login(member, "bob");
        var created = await Create(owner, "Personal history");
        var id = created.GetProperty("meeting").GetProperty("meetingId").GetGuid();
        var code = created.GetProperty("invitation").GetProperty("meetingCode").GetString();
        var path = $"/api/meetings/{id}";
        member.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");
        Assert.Equal(HttpStatusCode.NotFound, (await Post(member, path + "/rejoin", null)).StatusCode);
        var joined = await Body(await Post(member, "/api/meetings/join", new { meetingCode = code, displayName = "Member" }), HttpStatusCode.OK);
        var identity = joined.GetProperty("currentParticipant").GetProperty("participantId").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await member.DeleteAsync(path + "/history", TestContext.Current.CancellationToken)).StatusCode);
        await Message(owner, id, "keep");
        await Body(await Post(member, path + "/leave", new { operationId = Guid.NewGuid() }), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.NoContent, (await Post(member, path + "/rejoin", null)).StatusCode);
        var active = await member.GetFromJsonAsync<JsonElement>(path, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(identity, active.GetProperty("currentParticipant").GetProperty("participantId").GetGuid());
        await Body(await Post(member, path + "/leave", new { operationId = Guid.NewGuid() }), HttpStatusCode.OK);
        for (var attempt = 0; attempt < 2; attempt++)
            Assert.Equal(HttpStatusCode.NoContent, (await member.DeleteAsync(path + "/history", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Empty((await member.GetFromJsonAsync<JsonElement[]>("/api/meetings?view=history", cancellationToken: TestContext.Current.CancellationToken))!);
        Assert.Single((await owner.GetFromJsonAsync<JsonElement[]>("/api/meetings", cancellationToken: TestContext.Current.CancellationToken))!);
        Assert.Single((await owner.GetFromJsonAsync<JsonElement>(path + "/messages", cancellationToken: TestContext.Current.CancellationToken)).GetProperty("items").EnumerateArray());
        await Body(await Post(member, "/api/meetings/join", new { meetingCode = code, displayName = "Member" }), HttpStatusCode.OK);
        await Body(await Post(member, path + "/leave", new { operationId = Guid.NewGuid() }), HttpStatusCode.OK);
        Assert.Single((await member.GetFromJsonAsync<JsonElement[]>("/api/meetings?view=history", cancellationToken: TestContext.Current.CancellationToken))!);
    }

    [Fact]
    public async Task DeletePurgesBusinessRowsButKeepsOnlyVerifiableDeletionAndCreationReceipts()
    {
        using var factory = new TestApplicationFactory();
        using var owner = Client(factory); using var member = Client(factory); using var anonymous = Client(factory);
        await Login(owner, "alice"); await Login(member, "bob");
        var creationRequestId = Guid.NewGuid();
        var created = await Create(owner, "Delete all", creationRequestId);
        var other = await Create(owner, "Keep me");
        var id = created.GetProperty("meeting").GetProperty("meetingId").GetGuid();
        var otherId = other.GetProperty("meeting").GetProperty("meetingId").GetGuid();
        var code = created.GetProperty("invitation").GetProperty("meetingCode").GetString();
        var joined = await Body(await Post(member, "/api/meetings/join", new { meetingCode = code, displayName = "Member" }), HttpStatusCode.OK);
        var participantId = joined.GetProperty("currentParticipant").GetProperty("participantId").GetGuid();
        await Message(owner, id, "destroy this content");
        string proof;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            proof = (await scope.ServiceProvider.GetRequiredService<IMeetingLifecycleService>().IssueProofAsync(id, participantId, TestContext.Current.CancellationToken))!;
            Assert.NotNull(proof);
        }
        Assert.Equal("active", (await Body(await Post(anonymous, "/api/agent/v1/lifecycle-status", new { meetingId = id, lifecycleProof = proof }), HttpStatusCode.OK)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await Post(anonymous, "/api/agent/v1/lifecycle-status", new { meetingId = otherId, lifecycleProof = proof })).StatusCode);
        await Post(member, $"/api/meetings/{id}/leave", new { operationId = Guid.NewGuid() });
        var deletion = await Body(await Post(owner, $"/api/meetings/{id}/deletion", new { confirmationName = "Delete all" }), HttpStatusCode.Accepted);
        var statusUrl = deletion.GetProperty("statusUrl").GetString()!;
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync(statusUrl, TestContext.Current.CancellationToken)).StatusCode);
        await WaitDeleted(owner, statusUrl);
        Assert.Equal(HttpStatusCode.NoContent, (await Post(owner, $"/api/meetings/{id}/deletion", new { confirmationName = "Delete all" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/api/meetings/{id}/messages", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Empty((await member.GetFromJsonAsync<JsonElement[]>("/api/meetings?view=history", cancellationToken: TestContext.Current.CancellationToken))!);
        var final = await Body(await Post(anonymous, "/api/agent/v1/lifecycle-status", new { meetingId = id, lifecycleProof = proof }), HttpStatusCode.OK);
        Assert.Equal("deleted", final.GetProperty("status").GetString());
        Assert.Equal(2, final.EnumerateObject().Count());
        Assert.Equal(HttpStatusCode.NotFound, (await Post(anonymous, "/api/agent/v1/lifecycle-status", new { meetingId = id, lifecycleProof = new string('0', 64) })).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await Post(owner, "/api/meetings", new { creationRequestId, name = "Delete all" })).StatusCode);
        await using var check = factory.Services.CreateAsyncScope();
        var dbCheck = check.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        Assert.False(await dbCheck.Meetings.AnyAsync(m => m.ID == id, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await dbCheck.MeetingParticipants.AnyAsync(m => m.MeetingId == id, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await dbCheck.MeetingMessages.AnyAsync(m => m.MeetingId == id, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await dbCheck.Set<MeetingLeaveOperation>().AnyAsync(m => m.MeetingId == id, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True((await dbCheck.Set<MeetingDeletionOperation>().SingleAsync(m => m.MeetingId == id, cancellationToken: TestContext.Current.CancellationToken)).Completed);
        Assert.NotEqual(proof, (await dbCheck.Set<MeetingLifecycleProof>().SingleAsync(m => m.MeetingId == id, cancellationToken: TestContext.Current.CancellationToken)).ProofHash);
        Assert.Null((await dbCheck.MeetingCreationReceipts.SingleAsync(m => m.CreationRequestId == creationRequestId, cancellationToken: TestContext.Current.CancellationToken)).ExpiresAtUtc);
        Assert.True(await dbCheck.Meetings.AnyAsync(m => m.ID == otherId, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, await dbCheck.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FailedResourceCleanupKeepsBarrierAndResumesWithoutPrematureCompletion()
    {
        using var baseFactory = new TestApplicationFactory();
        var cleanup = new ControlledCleanup();
        using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<IMeetingDeletionResourceCleanup>(cleanup)));
        using var owner = Client(factory);
        await Login(owner, "alice");
        var created = await Create(owner, "Recover cleanup");
        var id = created.GetProperty("meeting").GetProperty("meetingId").GetGuid();
        await Message(owner, id, "must remain inaccessible until purged");
        var deletion = await Body(await Post(owner, $"/api/meetings/{id}/deletion", new { confirmationName = "Recover cleanup" }), HttpStatusCode.Accepted);
        await cleanup.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var url = deletion.GetProperty("statusUrl").GetString()!;
        Assert.Equal("deleting", (await owner.GetFromJsonAsync<JsonElement>(url, cancellationToken: TestContext.Current.CancellationToken)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Gone, (await owner.GetAsync($"/api/meetings/{id}/messages", TestContext.Current.CancellationToken)).StatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            Assert.True(await db.MeetingMessages.AnyAsync(m => m.MeetingId == id, cancellationToken: TestContext.Current.CancellationToken));
            Assert.False((await db.Set<MeetingDeletionOperation>().SingleAsync(m => m.MeetingId == id, cancellationToken: TestContext.Current.CancellationToken)).Completed);
        }
        cleanup.AllowCompletion = true;
        factory.Services.GetRequiredService<IMeetingDeletionSignal>().Notify();
        await WaitDeleted(owner, url);
        await using var final = factory.Services.CreateAsyncScope();
        Assert.False(await final.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>().MeetingMessages.AnyAsync(m => m.MeetingId == id, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OpenApiDescribesLifecycleResponsesForGeneratedClients()
    {
        using var factory = new TestApplicationFactory();
        using var client = Client(factory);
        var json = await client.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        var paths = document.RootElement.GetProperty("paths");
        foreach (var (path, method, code) in new[]
        {
            ("/api/meetings/{meetingId}/messages", "get", "410"),
            ("/api/meetings/{meetingId}/messages", "post", "410"),
            ("/api/meetings/{meetingId}/leave", "post", "410"),
            ("/api/meetings/{meetingId}/deletion", "post", "202"),
            ("/api/meetings/{meetingId}/deletion/{operationId}", "get", "200"),
            ("/api/agent/v1/lifecycle-status", "post", "200")
        })
        {
            var response = paths.GetProperty(path).GetProperty(method).GetProperty("responses").GetProperty(code);
            Assert.True(response.TryGetProperty("content", out var content));
            Assert.NotEmpty(content.EnumerateObject());
        }
        var exportPath = Environment.GetEnvironmentVariable("ROUND_TABLE_TEST_OPENAPI_OUTPUT");
        if (!string.IsNullOrWhiteSpace(exportPath)) await File.WriteAllTextAsync(exportPath, json, TestContext.Current.CancellationToken);
    }

    private sealed class ControlledCleanup : IMeetingDeletionResourceCleanup
    {
        public TaskCompletionSource Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool AllowCompletion;
        public Task DeleteForMeetingAsync(Guid meetingId, CancellationToken cancellationToken = default)
        {
            Attempted.TrySetResult();
            return AllowCompletion ? Task.CompletedTask : Task.FromException(new IOException("Injected cleanup failure"));
        }
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory) => factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });
    private static async Task Login(HttpClient client, string name) => await Body(await Post(client, "/api/auth/login", new { username = name, password = "test-password-" + name }), HttpStatusCode.OK);
    private static async Task<JsonElement> Create(HttpClient client, string name, Guid? requestId = null)
        => await Body(await Post(client, "/api/meetings", new { creationRequestId = requestId ?? Guid.NewGuid(), name }), HttpStatusCode.Created);
    private static async Task Message(HttpClient client, Guid id, string content)
        => await Body(await Post(client, $"/api/meetings/{id}/messages", new { clientSubmissionId = Guid.NewGuid(), content }), HttpStatusCode.Created);
    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, object? body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("Origin", "http://localhost:5173");
        if (body is not null) request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }
    private static async Task<JsonElement> Body(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {expected}, got {response.StatusCode}: {text}");
        return JsonSerializer.Deserialize<JsonElement>(text);
    }
    private static async Task WaitDeleted(HttpClient owner, string statusUrl)
    {
        for (var i = 0; i < 100; i++)
        {
            var status = await owner.GetFromJsonAsync<JsonElement>(statusUrl);
            if (status.GetProperty("status").GetString() == "deleted") return;
            await Task.Delay(50);
        }
        Assert.Fail("Deletion did not complete.");
    }
}
