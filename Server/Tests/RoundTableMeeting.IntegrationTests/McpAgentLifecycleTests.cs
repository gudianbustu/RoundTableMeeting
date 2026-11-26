using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.AgentCollaboration;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Fact]
    public async Task DeletingMeetingWakesIdleMcpWaitAndCleansAllAgentResources()
    {
        using var factory = new TestApplicationFactory();
        using var browser = CreateBrowserClient(factory);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Delete idle Agent");
        using var agent = CreateAnonymousClient(factory);
        var joined = await JoinLifecycleAgentAsync(agent, meeting.MeetingCode, "IdleAgent");
        var wait = CallToolAsync(agent, joined.Session.TransportSessionId, 3, "wait_for_mention",
            new { connectionSessionId = joined.ConnectionId }, useSessionEventStream: true);
        await EventuallyAsync(factory, async db => await db.AgentInitialContextDeliveries
            .AnyAsync(item => item.ConnectionSessionId == joined.ConnectionId && item.State == InitialContextState.Confirmed));
        await DeleteLifecycleMeetingAsync(browser, meeting.MeetingId, "Delete idle Agent");
        AssertToolError(await wait.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "meeting_not_found");
        await AssertAgentMeetingGoneAsync(factory, meeting.MeetingId);
        Assert.Empty(factory.Services.GetRequiredService<IAgentConnectionRegistry>().ListRegistrations(meeting.MeetingId));
        AssertToolError(await CallToolAsync(agent, joined.Session.TransportSessionId, 4, "get_agent_status",
            new { connectionSessionId = joined.ConnectionId }), "meeting_not_found");
    }

    [Fact]
    public async Task DeletingMeetingWithProcessingAndQueuedTasksDoesNotAffectAnotherMeeting()
    {
        using var factory = new TestApplicationFactory();
        using var browser = CreateBrowserClient(factory);
        await LoginAsync(browser, "alice", "test-password-alice");
        var first = await CreateMeetingAsync(browser, "Delete tasks");
        var second = await CreateMeetingAsync(browser, "Keep tasks");
        using var agent = CreateAnonymousClient(factory);
        using var other = CreateAnonymousClient(factory);
        var joined = await JoinLifecycleAgentAsync(agent, first.MeetingCode, "Worker");
        var independent = await JoinLifecycleAgentAsync(other, second.MeetingCode, "Worker");
        await PostHumanMessageAsync(browser, first.MeetingId, "@Worker first");
        await PostHumanMessageAsync(browser, first.MeetingId, "@Worker second");
        var task = Data(await CallToolAsync(agent, joined.Session.TransportSessionId, 3, "wait_for_mention",
            new { connectionSessionId = joined.ConnectionId }, useSessionEventStream: true));
        await DeleteLifecycleMeetingAsync(browser, first.MeetingId, "Delete tasks");
        await AssertAgentMeetingGoneAsync(factory, first.MeetingId);
        AssertToolError(await ReplyLifecycleAsync(agent, joined, task, 4, "too late"), "meeting_not_found");
        await PostHumanMessageAsync(browser, second.MeetingId, "@Worker still available");
        var remaining = Data(await CallToolAsync(other, independent.Session.TransportSessionId, 3, "wait_for_mention",
            new { connectionSessionId = independent.ConnectionId }, useSessionEventStream: true));
        Assert.True((await ReplyLifecycleAsync(other, independent, remaining, 4, "available")).Payload.GetProperty("succeeded").GetBoolean());
    }

    [Fact]
    public async Task FifoRejectsOvertakingAndReplyReplayValidatesAttemptAndContent()
    {
        using var factory = new TestApplicationFactory();
        using var browser = CreateBrowserClient(factory);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "FIFO");
        using var agent = CreateAnonymousClient(factory);
        using var outsider = CreateAnonymousClient(factory);
        var joined = await JoinLifecycleAgentAsync(agent, meeting.MeetingCode, "Worker");
        var foreign = await InitializeAsync(outsider);
        AssertToolError(await CallToolAsync(outsider, foreign.TransportSessionId, 2, "get_agent_status",
            new { connectionSessionId = joined.ConnectionId }), "connection_invalid");
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker first");
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker second");
        var first = Data(await CallToolAsync(agent, joined.Session.TransportSessionId, 3, "wait_for_mention",
            new { connectionSessionId = joined.ConnectionId }, useSessionEventStream: true));
        AssertToolError(await CallToolAsync(agent, joined.Session.TransportSessionId, 4, "wait_for_mention",
            new { connectionSessionId = joined.ConnectionId }), "task_not_available");
        var reply = Data(await ReplyLifecycleAsync(agent, joined, first, 5, "first result"));
        var replay = Data(await ReplyLifecycleAsync(agent, joined, first, 6, "first result"));
        Assert.Equal(reply.GetProperty("message").GetProperty("messageId").GetGuid(), replay.GetProperty("message").GetProperty("messageId").GetGuid());
        AssertToolError(await ReplyLifecycleAsync(agent, joined, first, 7, "different result"), "reply_idempotency_mismatch");
        AssertToolError(await ReplyLifecycleAsync(agent, joined, first, 8, "first result", Guid.NewGuid()), "delivery_attempt_invalid");
        var second = Data(await CallToolAsync(agent, joined.Session.TransportSessionId, 9, "wait_for_mention",
            new { connectionSessionId = joined.ConnectionId }));
        Assert.True(second.GetProperty("triggerSequence").GetInt64() > first.GetProperty("triggerSequence").GetInt64());
        Assert.NotEqual(first.GetProperty("taskId").GetGuid(), second.GetProperty("taskId").GetGuid());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeliveredAndFailedQueueHeadsAreObservableAndCannotBeSkipped(bool failed)
    {
        using var factory = new TestApplicationFactory();
        using var browser = CreateBrowserClient(factory);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Blocked queue");
        using var agent = CreateAnonymousClient(factory);
        var joined = await JoinLifecycleAgentAsync(agent, meeting.MeetingCode, "Worker");
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker first");
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker second");
        await EventuallyAsync(factory, async db => await db.AgentReplyTasks.CountAsync(item => item.MeetingId == meeting.MeetingId) == 2);
        Guid headId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            var head = await db.AgentReplyTasks.AsTracking().Where(item => item.MeetingId == meeting.MeetingId).OrderBy(item => item.TriggerSequence).FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
            headId = head.ID;
            if (failed) head.MarkFailed("context_invalid", DateTimeOffset.UtcNow);
            else head.MarkDelivered(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        AssertToolError(await CallToolAsync(agent, joined.Session.TransportSessionId, 3, "wait_for_mention",
            new { connectionSessionId = joined.ConnectionId }), "task_not_available");
        var status = Data(await CallToolAsync(agent, joined.Session.TransportSessionId, 4, "get_agent_status",
            new { connectionSessionId = joined.ConnectionId }));
        Assert.Equal(headId, status.GetProperty("queueHead").GetProperty("taskId").GetGuid());
        Assert.Equal(failed ? "context_invalid" : "delivery_not_finalized", status.GetProperty("queueHead").GetProperty("failureCode").GetString());
        await using var readScope = factory.Services.CreateAsyncScope();
        var activity = Assert.Single(await readScope.ServiceProvider.GetRequiredService<IAgentActivityService>().ListAsync(meeting.MeetingId, TestContext.Current.CancellationToken));
        Assert.Equal("blocked", activity.State);
        Assert.Equal(failed ? "context_invalid" : "delivery_not_finalized", activity.FailureCode);
    }

    [Fact]
    public async Task StartupReconcilesPersistedConnectionsAndPeriodicScanPreservesLiveBindings()
    {
        using var factory = new TestApplicationFactory();
        using var browser = CreateBrowserClient(factory);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Restart boundary");
        using var agent = CreateAnonymousClient(factory);
        var joined = await JoinLifecycleAgentAsync(agent, meeting.MeetingCode, "Worker");
        var reconciler = Assert.Single(factory.Services.GetServices<IHostedService>().OfType<AgentConnectionStartupReconciler>());
        await reconciler.ReconcileAsync(TestContext.Current.CancellationToken);
        var status = Data(await CallToolAsync(agent, joined.Session.TransportSessionId, 3, "get_agent_status",
            new { connectionSessionId = joined.ConnectionId }));
        Assert.Equal("connected", status.GetProperty("connection").GetProperty("state").GetString());
        var copyDirectory = Path.Combine(Path.GetTempPath(), "RoundTableMeetingTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(copyDirectory);
        var copyPath = Path.Combine(copyDirectory, "restart.db");
        try
        {
            using (var source = new SqliteConnection($"Data Source={factory.DatabasePath};Pooling=False"))
            using (var destination = new SqliteConnection($"Data Source={copyPath};Pooling=False"))
            {
                source.Open(); destination.Open(); source.BackupDatabase(destination);
            }
            using var restarted = new TestApplicationFactory(copyPath);
            using var restartClient = CreateAnonymousClient(restarted);
            await using var scope = restarted.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            var connection = await db.AgentConnectionSessions.SingleAsync(item => item.ID == joined.ConnectionId, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(AgentConnectionState.Disconnected, connection.State);
            Assert.True(await db.MeetingParticipants.AnyAsync(item => item.ID == connection.ParticipantId, cancellationToken: TestContext.Current.CancellationToken));
            var session = await InitializeAsync(restartClient);
            AssertToolError(await CallToolAsync(restartClient, session.TransportSessionId, 2, "join_meeting",
                new { meetingCode = meeting.MeetingCode, displayName = "Worker", contextCharacterLimit = 12000 }), "agent_recovery_not_supported");
        }
        finally { Directory.Delete(copyDirectory, recursive: true); }
    }

    private sealed record LifecycleAgent(McpSession Session, Guid ConnectionId);

    [Fact]
    public async Task ConcurrentSameNameJoinsCreateOnlyOneAgentConnectionAndCursor()
    {
        using var factory = new TestApplicationFactory();
        using var browser = CreateBrowserClient(factory);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Concurrent join");
        using var first = CreateAnonymousClient(factory);
        using var second = CreateAnonymousClient(factory);
        var sessions = await Task.WhenAll(InitializeAsync(first), InitializeAsync(second));
        var results = await Task.WhenAll(
            CallToolAsync(first, sessions[0].TransportSessionId, 2, "join_meeting",
                new { meetingCode = meeting.MeetingCode, displayName = "Worker", contextCharacterLimit = 12000 }),
            CallToolAsync(second, sessions[1].TransportSessionId, 2, "join_meeting",
                new { meetingCode = meeting.MeetingCode, displayName = "worker", contextCharacterLimit = 12000 }));
        Assert.Single(results, item => item.Payload.GetProperty("succeeded").GetBoolean());
        AssertToolError(Assert.Single(results, item => !item.Payload.GetProperty("succeeded").GetBoolean()), "agent_connection_already_active");
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        Assert.Equal(1, await db.MeetingParticipants.CountAsync(item => item.MeetingId == meeting.MeetingId && item.ParticipantType == ParticipantType.Agent, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await db.AgentConnectionSessions.CountAsync(item => item.MeetingId == meeting.MeetingId, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await db.AgentContextCursors.CountAsync(item => item.MeetingId == meeting.MeetingId, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentWaitsAndRepliesProduceOneAttemptAndOneReply()
    {
        using var factory = new TestApplicationFactory();
        using var browser = CreateBrowserClient(factory);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Concurrent task");
        using var agent = CreateAnonymousClient(factory);
        var joined = await JoinLifecycleAgentAsync(agent, meeting.MeetingCode, "Worker");
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker concurrent task");
        await EventuallyAsync(factory, async db => await db.AgentReplyTasks.AnyAsync(item => item.MeetingId == meeting.MeetingId));
        var waits = await Task.WhenAll(
            CallToolAsync(agent, joined.Session.TransportSessionId, 3, "wait_for_mention", new { connectionSessionId = joined.ConnectionId }),
            CallToolAsync(agent, joined.Session.TransportSessionId, 4, "wait_for_mention", new { connectionSessionId = joined.ConnectionId }));
        var task = Data(Assert.Single(waits, item => item.Payload.GetProperty("succeeded").GetBoolean()));
        AssertToolError(Assert.Single(waits, item => !item.Payload.GetProperty("succeeded").GetBoolean()), "task_not_available");
        var replies = await Task.WhenAll(
            ReplyLifecycleAsync(agent, joined, task, 5, "one result"),
            ReplyLifecycleAsync(agent, joined, task, 6, "one result"));
        Assert.Equal(Data(replies[0]).GetProperty("message").GetProperty("messageId").GetGuid(),
            Data(replies[1]).GetProperty("message").GetProperty("messageId").GetGuid());
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        Assert.Equal(1, await db.AgentTaskDeliveryAttempts.CountAsync(item => item.MeetingId == meeting.MeetingId, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, await db.MeetingMessages.CountAsync(item => item.MeetingId == meeting.MeetingId, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(AgentReplyTaskState.Replied, (await db.AgentReplyTasks.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).State);
    }

    [Fact]
    public async Task RestartFinishesDeletingMeetingWithPersistedAgentTask()
    {
        using var factory = new TestApplicationFactory();
        using var browser = CreateBrowserClient(factory);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Restart deletion");
        using var agent = CreateAnonymousClient(factory);
        var joined = await JoinLifecycleAgentAsync(agent, meeting.MeetingCode, "Worker");
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker task before deletion");
        Data(await CallToolAsync(agent, joined.Session.TransportSessionId, 3, "wait_for_mention",
            new { connectionSessionId = joined.ConnectionId }, useSessionEventStream: true));
        var recovery = Assert.Single(factory.Services.GetServices<IHostedService>()
            .OfType<RoundTableMeeting.Meetings.MeetingDeletionRecoveryService>());
        await recovery.StopAsync(CancellationToken.None);
        await recovery.ExecuteTask!;
        Assert.False(factory.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
        await DeleteLifecycleMeetingAsync(browser, meeting.MeetingId, "Restart deletion");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            Assert.Equal(MeetingLifecycleState.Deleting, (await db.Meetings.SingleAsync(item => item.ID == meeting.MeetingId, cancellationToken: TestContext.Current.CancellationToken)).LifecycleState);
            Assert.Equal(AgentReplyTaskState.Processing, (await db.AgentReplyTasks.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).State);
        }
        var directory = Path.Combine(Path.GetTempPath(), "RoundTableMeetingTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var copy = Path.Combine(directory, "deleting.db");
        try
        {
            using (var source = new SqliteConnection($"Data Source={factory.DatabasePath};Pooling=False"))
            using (var destination = new SqliteConnection($"Data Source={copy};Pooling=False"))
            {
                source.Open(); destination.Open(); source.BackupDatabase(destination);
            }
            using var restarted = new TestApplicationFactory(copy);
            using var client = CreateAnonymousClient(restarted);
            await AssertAgentMeetingGoneAsync(restarted, meeting.MeetingId);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task<LifecycleAgent> JoinLifecycleAgentAsync(HttpClient agent, string meetingCode, string name)
    {
        var session = await InitializeAsync(agent);
        var data = Data(await CallToolAsync(agent, session.TransportSessionId, 2, "join_meeting",
            new { meetingCode, displayName = name, contextCharacterLimit = 12000 }));
        return new(session, data.GetProperty("connection").GetProperty("connectionSessionId").GetGuid());
    }

    private static JsonElement Data(ToolCall result)
    {
        Assert.True(result.Payload.GetProperty("succeeded").GetBoolean());
        return result.Payload.GetProperty("data");
    }

    private static void AssertToolError(ToolCall result, string code)
    {
        Assert.False(result.Payload.GetProperty("succeeded").GetBoolean());
        Assert.Equal(code, result.Payload.GetProperty("errorCode").GetString());
    }

    private static Task<ToolCall> ReplyLifecycleAsync(HttpClient agent, LifecycleAgent joined, JsonElement task, int id, string content, Guid? attempt = null)
        => CallToolAsync(agent, joined.Session.TransportSessionId, id, "reply_to_mention", new
        {
            connectionSessionId = joined.ConnectionId,
            taskId = task.GetProperty("taskId").GetGuid(),
            deliveryAttemptId = attempt ?? task.GetProperty("deliveryAttemptId").GetGuid(),
            replyIdempotencyKey = task.GetProperty("replyIdempotencyKey").GetGuid(), content
        });

    private static async Task DeleteLifecycleMeetingAsync(HttpClient browser, Guid meetingId, string name)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/meetings/{meetingId}/deletion")
        { Content = JsonContent.Create(new { confirmationName = name }) };
        request.Headers.Add("Origin", "http://localhost:5173");
        Assert.Equal(HttpStatusCode.Accepted, (await browser.SendAsync(request)).StatusCode);
    }

    private static async Task AssertAgentMeetingGoneAsync(WebApplicationFactory<Program> factory, Guid meetingId)
    {
        await EventuallyAsync(factory, async db => !await db.Meetings.AnyAsync(item => item.ID == meetingId));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        Assert.False(await db.AgentTaskDeliveryAttempts.AnyAsync(item => item.MeetingId == meetingId));
        Assert.False(await db.AgentInitialContextDeliveries.AnyAsync(item => item.MeetingId == meetingId));
        Assert.False(await db.AgentRecoveryOperations.AnyAsync(item => item.MeetingId == meetingId));
        Assert.False(await db.AgentReplyTasks.AnyAsync(item => item.MeetingId == meetingId));
        Assert.False(await db.AgentContextCursors.AnyAsync(item => item.MeetingId == meetingId));
        Assert.False(await db.AgentConnectionSessions.AnyAsync(item => item.MeetingId == meetingId));
        Assert.False(await db.MeetingMessages.AnyAsync(item => item.MeetingId == meetingId));
        Assert.False(await db.MeetingParticipants.AnyAsync(item => item.MeetingId == meetingId));
    }

    private static async Task EventuallyAsync(WebApplicationFactory<Program> factory, Func<RoundTableMeetingDbContext, Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!timeout.IsCancellationRequested)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            if (await condition(scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>())) return;
            await Task.Delay(50, timeout.Token);
        }
        Assert.Fail("The persisted condition did not converge.");
    }
}
