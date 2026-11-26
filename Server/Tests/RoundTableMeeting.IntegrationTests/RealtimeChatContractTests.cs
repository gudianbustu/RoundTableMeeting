using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed class RealtimeChatContractTests
{
    [Fact]
    public async Task PostedMessageIsCompletedByDurableDispatcher()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        await LoginAsync(client);
        using var create = JsonPost("/api/meetings", new { creationRequestId = Guid.NewGuid(), name = "Delivery" });
        var meetingId = (await (await client.SendAsync(create, TestContext.Current.CancellationToken)).Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken)).GetProperty("meeting").GetProperty("meetingId").GetGuid();
        using var message = JsonPost($"/api/meetings/{meetingId}/messages", new { clientSubmissionId = Guid.NewGuid(), content = "dispatch me" });
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(message, TestContext.Current.CancellationToken)).StatusCode);

        for (var attempt = 0; attempt < 50; attempt++)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            if (await context.MessageEventDeliveries.AnyAsync(delivery => delivery.State == MessageEventDeliveryState.Completed, cancellationToken: TestContext.Current.CancellationToken)) return;
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        Assert.Fail("The durable message delivery was not completed by the dispatcher.");
    }

    [Fact]
    public async Task MessageSubmissionIsIdempotentAndMentionsUseFrozenFieldNames()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        await LoginAsync(client);
        using var create = JsonPost("/api/meetings", new { creationRequestId = Guid.NewGuid(), name = "Idempotency" });
        var created = await (await client.SendAsync(create, TestContext.Current.CancellationToken)).Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        var meetingId = created.GetProperty("meeting").GetProperty("meetingId").GetGuid();
        var participant = created.GetProperty("meeting").GetProperty("participants")[0];
        var submissionId = Guid.NewGuid();
        using var first = JsonPost($"/api/meetings/{meetingId}/messages", new { clientSubmissionId = submissionId, content = "@Alice hello", mentionCandidates = new[] { new { participantId = participant.GetProperty("participantId").GetGuid(), startOffset = 0, length = 6 } } });
        var firstResponse = await client.SendAsync(first, TestContext.Current.CancellationToken);
        using var replay = JsonPost($"/api/meetings/{meetingId}/messages", new { clientSubmissionId = submissionId, content = "@Alice hello", mentionCandidates = new[] { new { participantId = participant.GetProperty("participantId").GetGuid(), startOffset = 0, length = 6 } } });
        var replayResponse = await client.SendAsync(replay, TestContext.Current.CancellationToken);
        using var mismatch = JsonPost($"/api/meetings/{meetingId}/messages", new { clientSubmissionId = submissionId, content = "different" });
        var mismatchResponse = await client.SendAsync(mismatch, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, mismatchResponse.StatusCode);
        var mention = (await firstResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken)).GetProperty("message").GetProperty("mentions")[0];
        Assert.True(mention.TryGetProperty("participantId", out _));
        Assert.True(mention.TryGetProperty("displayName", out _));
        Assert.False(mention.TryGetProperty("targetParticipantId", out _));
        Assert.Equal("message_submission_mismatch", (await mismatchResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task AnonymousAndNonMemberMessageAccessIsRejected()
    {
        using var factory = new TestApplicationFactory();
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        using var outsider = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        await LoginAsync(owner);
        using var create = JsonPost("/api/meetings", new { creationRequestId = Guid.NewGuid(), name = "Private" });
        var meetingId = (await (await owner.SendAsync(create, TestContext.Current.CancellationToken)).Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken)).GetProperty("meeting").GetProperty("meetingId").GetGuid();
        Assert.Equal(HttpStatusCode.Unauthorized, (await outsider.GetAsync($"/api/meetings/{meetingId}/messages", TestContext.Current.CancellationToken)).StatusCode);
        await LoginAsync(outsider, "bob", "test-password-bob");
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/meetings/{meetingId}/messages", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task HubNegotiateRequiresAuthenticationAndAllowedOriginAndIsNotCached()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        using var anonymous = new HttpRequestMessage(HttpMethod.Post, "/hubs/meeting-chat/negotiate?negotiateVersion=1");
        anonymous.Headers.TryAddWithoutValidation("Origin", "http://localhost:5173");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(anonymous, TestContext.Current.CancellationToken)).StatusCode);
        await LoginAsync(client);
        using var forbidden = new HttpRequestMessage(HttpMethod.Post, "/hubs/meeting-chat/negotiate?negotiateVersion=1");
        forbidden.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(forbidden, TestContext.Current.CancellationToken)).StatusCode);
        using var allowed = new HttpRequestMessage(HttpMethod.Post, "/hubs/meeting-chat/negotiate?negotiateVersion=1");
        allowed.Headers.TryAddWithoutValidation("Origin", "http://localhost:5173");
        var response = await client.SendAsync(allowed, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MessageBoundariesReturnValidationNoStoreAndPlainTextContent()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        await LoginAsync(client);
        using var create = JsonPost("/api/meetings", new { creationRequestId = Guid.NewGuid(), name = "Boundaries" });
        var meetingId = (await (await client.SendAsync(create, TestContext.Current.CancellationToken)).Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken)).GetProperty("meeting").GetProperty("meetingId").GetGuid();

        var invalid = await client.GetAsync($"/api/meetings/{meetingId}/messages?beforeSequence=1&afterSequence=1", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("validation_failed", (await invalid.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken)).GetProperty("code").GetString());
        Assert.Contains("no-store", invalid.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);

        const string content = "<script>alert('x')</script>";
        using var message = JsonPost($"/api/meetings/{meetingId}/messages", new { clientSubmissionId = Guid.NewGuid(), content });
        var response = await client.SendAsync(message, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(content, (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken)).GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public async Task AfterCursorWithoutThroughReportsRemainingMessages()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        await LoginAsync(client);
        using var create = JsonPost("/api/meetings", new { creationRequestId = Guid.NewGuid(), name = "History" });
        var created = await client.SendAsync(create, TestContext.Current.CancellationToken);
        var meetingId = (await created.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken)).GetProperty("meeting").GetProperty("meetingId").GetGuid();
        foreach (var content in new[] { "first", "second" })
        {
            using var message = JsonPost($"/api/meetings/{meetingId}/messages", new { clientSubmissionId = Guid.NewGuid(), content });
            Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(message, TestContext.Current.CancellationToken)).StatusCode);
        }

        var page = await client.GetFromJsonAsync<JsonElement>($"/api/meetings/{meetingId}/messages?afterSequence=0&limit=1", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(page.GetProperty("hasMoreAfter").GetBoolean());
        Assert.Equal(1, page.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task AuthenticatedParticipantMessagePostIsRateLimited()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        await LoginAsync(client);
        using var create = JsonPost("/api/meetings", new { creationRequestId = Guid.NewGuid(), name = "Chat" });
        var created = await client.SendAsync(create, TestContext.Current.CancellationToken);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        var meetingId = body.GetProperty("meeting").GetProperty("meetingId").GetGuid();
        for (var index = 0; index <= 60; index++)
        {
            using var message = JsonPost($"/api/meetings/{meetingId}/messages", new { clientSubmissionId = Guid.NewGuid(), content = $"message-{index}" });
            var response = await client.SendAsync(message, TestContext.Current.CancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Assert.True(response.Headers.TryGetValues("Retry-After", out var retry));
                Assert.True(int.Parse(retry.Single()) >= 1);
                Assert.Equal("message_send_rate_limited", (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken)).GetProperty("code").GetString());
                return;
            }
            Assert.True(response.StatusCode == HttpStatusCode.Created, (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)) + "\n" + string.Join("\n", factory.LogSink.Events.Select(item => item.RenderMessage() + " " + item.Exception)));
        }
        Assert.Fail("The message endpoint did not enforce its configured rate limit.");
    }

    private static Task LoginAsync(HttpClient client) => LoginAsync(client, "alice", "test-password-alice");
    private static async Task LoginAsync(HttpClient client, string username, string password)
    {
        using var request = JsonPost("/api/auth/login", new { username, password });
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
    }

    private static HttpRequestMessage JsonPost(string path, object value)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.TryAddWithoutValidation("Origin", "http://localhost:5173");
        request.Content = JsonContent.Create(value);
        return request;
    }
}
