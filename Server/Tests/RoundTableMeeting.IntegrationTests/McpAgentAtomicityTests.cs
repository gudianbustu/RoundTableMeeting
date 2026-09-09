using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Application.RealtimeChat;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;
using RoundTableMeeting.Domain.Entities;
using IUnitOfWork = RoundTableMeeting.Domain.UnitOfWork.IUnitOfWork;
using RoundTableMeeting.Repository.Sqlite.UnitOfWork;
using Materal.TTA.Common;
using RoundTableMeeting.Repository.Sqlite.Services;
using RoundTableMeeting.Meetings;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    private sealed class CleanupFailureSwitch
    {
        public bool Fail { get; set; } = true;
        public TaskCompletionSource Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class FaultingAgentCleanup(AgentCollaborationCleanupService core, CleanupFailureSwitch fault) : IAgentCollaborationCleanupService
    {
        public async Task DeleteForMeetingAsync(Guid meetingId, DateTimeOffset cancelledAtUtc, CancellationToken cancellationToken = default)
        {
            await core.DeleteForMeetingAsync(meetingId, cancelledAtUtc, cancellationToken);
            if (fault.Fail)
            {
                fault.Attempted.TrySetResult();
                throw new InvalidOperationException("injected-cleanup-failure");
            }
        }
    }

    [Fact]
    public async Task FailedAgentCleanupRollsBackAndRetryFinishesDeletingMeeting()
    {
        using var baseFactory = new TestApplicationFactory();
        var fault = new CleanupFailureSwitch();
        using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAgentCollaborationCleanupService>();
            services.AddSingleton(fault);
            services.AddScoped<AgentCollaborationCleanupService>();
            services.AddScoped<IAgentCollaborationCleanupService, FaultingAgentCleanup>();
        }));
        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using var agent = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Cleanup retry");
        var joined = await JoinLifecycleAgentAsync(agent, meeting.MeetingCode, "Worker");
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker task");
        var task = Data(await CallToolAsync(agent, joined.Session.TransportSessionId, 3, "wait_for_mention",
            new { connectionSessionId = joined.ConnectionId }, useSessionEventStream: true));
        await DeleteLifecycleMeetingAsync(browser, meeting.MeetingId, "Cleanup retry");
        await fault.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            Assert.Equal(MeetingLifecycleState.Deleting, (await db.Meetings.SingleAsync()).LifecycleState);
            Assert.Equal(AgentReplyTaskState.Processing, (await db.AgentReplyTasks.SingleAsync()).State);
            Assert.Equal(1, await db.AgentTaskDeliveryAttempts.CountAsync());
            Assert.Equal(1, await db.AgentConnectionSessions.CountAsync());
        }
        AssertToolError(await ReplyLifecycleAsync(agent, joined, task, 4, "late reply"), "meeting_not_found");
        fault.Fail = false;
        factory.Services.GetRequiredService<MeetingDeletionSignal>().Notify();
        await AssertAgentMeetingGoneAsync(factory, meeting.MeetingId);
    }

    private sealed class CommitFailureSwitch { public string? Stage { get; set; } }

    private sealed class FaultingAgentUnitOfWork(UnitOfWorkImpl core, RoundTableMeetingDbContext db, CommitFailureSwitch fault) : IUnitOfWork
    {
        public void RegisterAdd(IEntity<Guid> entity) => core.RegisterAdd(entity);
        public void RegisterEdit(IEntity<Guid> entity) => core.RegisterEdit(entity);
        public void RegisterDelete(IEntity<Guid> entity) => core.RegisterDelete(entity);
        public async Task<int> CommitAsync(CancellationToken cancellationToken = default)
        {
            var shouldFail = fault.Stage == "join" && db.ChangeTracker.Entries<AgentConnectionSession>().Any(item => item.State == EntityState.Added)
                || fault.Stage == "prepare" && db.ChangeTracker.Entries<AgentTaskDeliveryAttempt>().Any(item => item.State == EntityState.Added)
                || fault.Stage == "finalize" && db.ChangeTracker.Entries<AgentReplyTask>().Any(item => item.Entity.State == AgentReplyTaskState.Processing);
            var count = await core.CommitAsync(cancellationToken);
            if (shouldFail) throw new InvalidOperationException("injected-commit-failure");
            return count;
        }
    }

    [Theory]
    [InlineData("join")]
    [InlineData("prepare")]
    [InlineData("finalize")]
    public async Task FailedCommitPreservesJoinAtomicityOrDeliveredQueueHead(string stage)
    {
        using var baseFactory = new TestApplicationFactory();
        var fault = new CommitFailureSwitch();
        using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IUnitOfWork>();
            services.AddSingleton(fault);
            services.AddScoped<UnitOfWorkImpl>();
            services.AddScoped<IUnitOfWork, FaultingAgentUnitOfWork>();
        }));
        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using var agent = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Commit failure");
        if (stage == "prepare") await PostHumanMessageAsync(browser, meeting.MeetingId, "Initial context before join");
        var session = await InitializeAsync(agent);
        if (stage == "join") fault.Stage = stage;
        var join = await CallToolAsync(agent, session.TransportSessionId, 2, "join_meeting",
            new { meetingCode = meeting.MeetingCode, displayName = "Worker", contextCharacterLimit = 12000 });
        if (stage == "join")
        {
            AssertToolError(join, "agent_operation_unavailable");
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
                Assert.False(await db.AgentConnectionSessions.AnyAsync());
                Assert.False(await db.AgentContextCursors.AnyAsync());
                Assert.False(await db.MeetingParticipants.AnyAsync(item => item.ParticipantType == ParticipantType.Agent));
            }
            fault.Stage = null;
            join = await CallToolAsync(agent, session.TransportSessionId, 3, "join_meeting",
                new { meetingCode = meeting.MeetingCode, displayName = "Worker", contextCharacterLimit = 12000 });
            Assert.True(join.Payload.GetProperty("succeeded").GetBoolean());
            return;
        }
        var connectionId = Data(join).GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker first");
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker second");
        fault.Stage = stage;
        AssertToolError(await CallToolAsync(agent, session.TransportSessionId, 3, "wait_for_mention",
            new { connectionSessionId = connectionId }, useSessionEventStream: true), "agent_operation_unavailable");
        fault.Stage = null;
        if (stage == "prepare")
        {
            DateTimeOffset? confirmedAt;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
                var connection = await db.AgentConnectionSessions.SingleAsync();
                Assert.Equal(InitialContextState.Confirmed, connection.InitialContextState);
                confirmedAt = connection.InitialContextConfirmedAtUtc;
                Assert.NotNull(confirmedAt);
                Assert.Equal(1, (await db.AgentContextCursors.SingleAsync()).DeliveredThroughSequence);
                Assert.All(await db.AgentReplyTasks.ToArrayAsync(), item => Assert.Equal(AgentReplyTaskState.Queued, item.State));
                Assert.False(await db.AgentTaskDeliveryAttempts.AnyAsync());
            }
            var retry = Data(await CallToolAsync(agent, session.TransportSessionId, 4, "wait_for_mention", new { connectionSessionId = connectionId }));
            Assert.Equal(2, retry.GetProperty("triggerSequence").GetInt64());
            await using var readScope = factory.Services.CreateAsyncScope();
            var readDb = readScope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            Assert.Equal(confirmedAt, (await readDb.AgentConnectionSessions.SingleAsync()).InitialContextConfirmedAtUtc);
            return;
        }
        AssertToolError(await CallToolAsync(agent, session.TransportSessionId, 4, "wait_for_mention",
            new { connectionSessionId = connectionId }), "task_not_available");
        var status = Data(await CallToolAsync(agent, session.TransportSessionId, 5, "get_agent_status",
            new { connectionSessionId = connectionId }));
        Assert.Equal("delivery_not_finalized", status.GetProperty("queueHead").GetProperty("failureCode").GetString());
        await using var finalScope = factory.Services.CreateAsyncScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        Assert.Equal(new[] { AgentReplyTaskState.Delivered, AgentReplyTaskState.Queued },
            await finalDb.AgentReplyTasks.OrderBy(item => item.TriggerSequence).Select(item => item.State).ToArrayAsync());
        Assert.Equal(AgentTaskDeliveryAttemptState.Prepared, (await finalDb.AgentTaskDeliveryAttempts.SingleAsync()).State);
        Assert.Equal(0, (await finalDb.AgentContextCursors.SingleAsync()).DeliveredThroughSequence);
    }

    private sealed class AppendFailureSwitch { public bool Fail { get; set; } = true; }

    private sealed class FailingAgentAppendCore(ResolvedMessageAppendCore core, AppendFailureSwitch fault) : IResolvedMessageAppendCore
    {
        public Task<ResolvedMessageAppendResult> AppendAsync(Guid meetingId, ResolvedMessageAuthor author, string content,
            Guid clientSubmissionId, IReadOnlyList<MentionCandidateDto>? mentionCandidates = null, CancellationToken cancellationToken = default)
            => core.AppendAsync(meetingId, author, content, clientSubmissionId, mentionCandidates, cancellationToken);

        public async Task<ResolvedMessageAppendResult> AppendAgentAsync(Guid meetingId, Guid participantId, string authorDisplayName,
            string content, Guid clientSubmissionId, CancellationToken cancellationToken = default)
        {
            var result = await core.AppendAgentAsync(meetingId, participantId, authorDisplayName, content, clientSubmissionId, cancellationToken);
            if (fault.Fail) throw new InvalidOperationException("private-database-failure-details");
            return result;
        }
    }

    [Fact]
    public async Task FailedAgentReplyRollsBackMessagesMentionsEventsAndTaskAndCanBeRetried()
    {
        using var baseFactory = new TestApplicationFactory();
        var fault = new AppendFailureSwitch();
        using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IResolvedMessageAppendCore>();
            services.AddSingleton(fault);
            services.AddScoped<ResolvedMessageAppendCore>();
            services.AddScoped<IResolvedMessageAppendCore, FailingAgentAppendCore>();
        }));
        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using var agent = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Atomic Agent reply");
        var joined = await JoinLifecycleAgentAsync(agent, meeting.MeetingCode, "Worker");
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker first task");
        var task = Data(await CallToolAsync(agent, joined.Session.TransportSessionId, 3, "wait_for_mention",
            new { connectionSessionId = joined.ConnectionId }, useSessionEventStream: true));
        var failure = await ReplyLifecycleAsync(agent, joined, task, 4, "@Alice result");
        AssertToolError(failure, "agent_operation_unavailable");
        Assert.DoesNotContain("private-database", failure.Raw);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            Assert.Equal(1, await db.MeetingMessages.CountAsync(item => item.MeetingId == meeting.MeetingId));
            Assert.Equal(1, await db.MessageMentions.CountAsync(item => item.MeetingId == meeting.MeetingId));
            Assert.Equal(1, await db.MessageAppendedEvents.CountAsync(item => item.MeetingId == meeting.MeetingId));
            Assert.Equal(2, await db.MessageEventDeliveries.CountAsync());
            Assert.Equal(AgentReplyTaskState.Processing, (await db.AgentReplyTasks.SingleAsync()).State);
        }
        fault.Fail = false;
        var reply = Data(await ReplyLifecycleAsync(agent, joined, task, 5, "@Alice result"));
        Assert.Equal(2, reply.GetProperty("message").GetProperty("sequence").GetInt64());
        await using var finalScope = factory.Services.CreateAsyncScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        Assert.Equal(2, await finalDb.MeetingMessages.CountAsync());
        Assert.Equal(2, await finalDb.MessageMentions.CountAsync());
        Assert.Equal(2, await finalDb.MessageAppendedEvents.CountAsync());
        Assert.Equal(4, await finalDb.MessageEventDeliveries.CountAsync());
        Assert.Equal(AgentReplyTaskState.Replied, (await finalDb.AgentReplyTasks.SingleAsync()).State);
    }
}
