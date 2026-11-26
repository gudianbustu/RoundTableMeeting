using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed class MeetingWorkspaceContractTests
{
    private const string Origin = "http://localhost:5173";

    [Fact]
    public async Task CreationReplayChecksInitialModeAndPreservesTheCurrentMode()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient();
        await LoginAsync(client, "alice", "test-password-alice");
        client.DefaultRequestHeaders.Add("Origin", Origin);
        var id = Guid.NewGuid();
        var payload = new { creationRequestId = id, name = "Modes", initialDiscussionMode = "free" };
        using var created = await client.PostAsJsonAsync("/api/meetings", payload, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var result = await created.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        var meeting = result.GetProperty("meeting");
        Assert.True(meeting.GetProperty("freeDiscussion").GetBoolean());
        var meetingId = meeting.GetProperty("meetingId").GetGuid();
        using var changed = await client.PutAsJsonAsync($"/api/meetings/{meetingId}/discussion-mode", new { mode = "directed" }, cancellationToken: TestContext.Current.CancellationToken);
        changed.EnsureSuccessStatusCode();
        using var replay = await client.PostAsJsonAsync("/api/meetings", payload, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = await replay.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(replayed.GetProperty("meeting").GetProperty("freeDiscussion").GetBoolean());
        using var conflict = await client.PostAsJsonAsync("/api/meetings", new { creationRequestId = id, name = "Modes", initialDiscussionMode = "directed" }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task TwoAccountsCanCreateJoinAndDeleteMeeting()
    {
        using var factory = new TestApplicationFactory();
        using var alice = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        using var bob = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        await LoginAsync(alice, "alice", "test-password-alice");
        await LoginAsync(bob, "bob", "test-password-bob");

        using var create = JsonPost("/api/meetings", new { creationRequestId = Guid.NewGuid(), name = "Architecture", description = "Review" });
        var created = await alice.SendAsync(create, TestContext.Current.CancellationToken);
        var createBody = await created.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.NotNull(created.Headers.Location);
        var meetingId = createBody.GetProperty("meeting").GetProperty("meetingId").GetGuid();
        var meetingCode = createBody.GetProperty("invitation").GetProperty("meetingCode").GetString();
        Assert.True(Guid.TryParseExact(meetingCode, "D", out _));
        Assert.True(createBody.GetProperty("meeting").GetProperty("canManageInvitation").GetBoolean());
        using var outsiderInvitation = JsonPost($"/api/meetings/{meetingId}/invitation", null);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.SendAsync(outsiderInvitation, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(1, createBody.GetProperty("meeting").GetProperty("participants").GetArrayLength());
        var meetingList = await alice.GetFromJsonAsync<JsonElement[]>("/api/meetings", cancellationToken: TestContext.Current.CancellationToken);
        var list = Assert.IsType<JsonElement[]>(meetingList);
        Assert.Single(list);
        Assert.Equal(meetingId, list[0].GetProperty("meetingId").GetGuid());

        using var preview = JsonPost("/api/meetings/join/preview", new { meetingCode });
        Assert.Equal(HttpStatusCode.OK, (await bob.SendAsync(preview, TestContext.Current.CancellationToken)).StatusCode);
        using var join = JsonPost("/api/meetings/join", new { meetingCode, displayName = "Bob at table" });
        var joined = await bob.SendAsync(join, TestContext.Current.CancellationToken);
        var joinBody = await joined.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        Assert.Equal(2, joinBody.GetProperty("participants").GetArrayLength());
        Assert.False(joinBody.TryGetProperty("meetingCode", out _));
        Assert.True(joinBody.GetProperty("canManageInvitation").GetBoolean());
        Assert.False(joinBody.GetProperty("canDelete").GetBoolean());
        var details = await bob.GetFromJsonAsync<JsonElement>($"/api/meetings/{meetingId}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(details.GetProperty("canManageInvitation").GetBoolean());

        using var invitation = JsonPost($"/api/meetings/{meetingId}/invitation", null);
        var invitationResponse = await bob.SendAsync(invitation, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, invitationResponse.StatusCode);
        Assert.True(invitationResponse.Headers.CacheControl?.NoStore);
        Assert.Equal(meetingCode, (await invitationResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken)).GetProperty("meetingCode").GetString());
        using var deniedDeletion = JsonPost($"/api/meetings/{meetingId}/deletion", new { confirmationName = "Architecture" });
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.SendAsync(deniedDeletion, TestContext.Current.CancellationToken)).StatusCode);

        using var deletion = JsonPost($"/api/meetings/{meetingId}/deletion", new { confirmationName = "Architecture" });
        Assert.Equal(HttpStatusCode.Accepted, (await alice.SendAsync(deletion, TestContext.Current.CancellationToken)).StatusCode);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var response = await bob.GetAsync($"/api/meetings/{meetingId}", TestContext.Current.CancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) break;
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/meetings/{meetingId}", TestContext.Current.CancellationToken)).StatusCode);
        using var deletedInvitation = JsonPost($"/api/meetings/{meetingId}/invitation", null);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.SendAsync(deletedInvitation, TestContext.Current.CancellationToken)).StatusCode);
        using var oldCode = JsonPost("/api/meetings/join/preview", new { meetingCode });
        Assert.Equal(HttpStatusCode.NotFound, (await bob.SendAsync(oldCode, TestContext.Current.CancellationToken)).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        Assert.False(await context.MeetingMessages.AnyAsync(message => message.MeetingId == meetingId, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await context.MessageMentions.AnyAsync(mention => mention.MeetingId == meetingId, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await context.MessageAppendedEvents.AnyAsync(item => item.MeetingId == meetingId, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InvalidGuidIsHiddenAndJoinRequestsAreRateLimitedAfterAuthentication()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        await LoginAsync(client, "alice", "test-password-alice");
        for (var index = 0; index < 30; index++)
        {
            using var request = JsonPost("/api/meetings/join/preview", new { meetingCode = "not-a-guid" });
            Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode);
        }
        using var limited = JsonPost("/api/meetings/join/preview", new { meetingCode = "not-a-guid" });
        var response = await client.SendAsync(limited, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Retry-After", out var values));
        Assert.True(int.Parse(values.Single()) >= 1);
        Assert.Equal("meeting_join_rate_limited", (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task OpenApiDeclaresRetryAfterForRateLimitedOperations()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient();
        var document = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json", cancellationToken: TestContext.Current.CancellationToken);
        var paths = document.GetProperty("paths");
        foreach (var path in new[] { "/api/meetings/join", "/api/meetings/join/preview", "/api/meetings/{meetingId}/messages" })
        {
            var headers = paths.GetProperty(path).GetProperty("post").GetProperty("responses").GetProperty("429").GetProperty("headers");
            Assert.True(headers.TryGetProperty("Retry-After", out _));
        }
        Assert.False(paths.GetProperty("/api/meetings").GetProperty("post").GetProperty("responses").TryGetProperty("429", out _));
        Assert.True(paths.GetProperty("/api/meetings/{meetingId}/deletion").GetProperty("post").GetProperty("responses").TryGetProperty("409", out _));
        Assert.False(paths.GetProperty("/api/meetings/{meetingId}").GetProperty("get").GetProperty("responses").TryGetProperty("409", out _));
    }

    private static async Task LoginAsync(HttpClient client, string username, string password)
    {
        using var request = JsonPost("/api/auth/login", new { username, password });
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
    }

    private static HttpRequestMessage JsonPost(string path, object? value)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.TryAddWithoutValidation("Origin", Origin);
        if (value is not null) request.Content = JsonContent.Create(value);
        return request;
    }
}
