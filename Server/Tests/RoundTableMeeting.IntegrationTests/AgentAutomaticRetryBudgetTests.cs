using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;
using RoundTableMeeting.Application.AgentCollaboration;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task AutomaticRetryQuotaExhaustsPerCycleAndRecoveryCannotBypassTheFailedHead(int quota)
    {
        using var test = await ReliableRaceFixture.CreateAsync();
        var options = test.Services.GetRequiredService<AgentCollaborationOptions>();
        options.MaximumAutomaticRetries = quota;
        options.RetryBackoffSeconds = [1];
        using var browser = test.Factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        await PostHumanMessageAsync(browser, test.MeetingId, "@Worker Later FIFO task");
        await EventuallyAsync(test.Factory, async db => await db.AgentReplyTasks.CountAsync() == 2);
        var connectionId = test.ConnectionId;
        var transport = test.Transport;
        var current = test.Mention;
        var firstKey = current.ReplyIdempotencyKey;
        for (var cycle = 0; cycle < 2; cycle++)
        {
            for (var automatic = 0; automatic <= quota; automatic++)
            {
                var decision = Assert.IsType<AgentFailureResultDto>((await test.Run(s => s.ReportFailureReliableAsync(
                    new(connectionId, current.TaskId, current.DeliveryAttemptId, "agent_transient_failure"), transport))).Data);
                Assert.Equal(cycle, decision.RetryCycle);
                Assert.Equal(automatic == quota, decision.RetryExhausted);
                var rejection = await test.Run(s => s.WaitReliableAsync(new(connectionId), transport));
                Assert.Equal(automatic == quota ? "retry_exhausted" : "retry_pending", rejection.ErrorCode);
                await using (var scope = test.Services.CreateAsyncScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
                    var tasks = await db.AgentReplyTasks.OrderBy(t => t.TriggerSequence).ToArrayAsync();
                    Assert.Equal(AgentReplyTaskState.Failed, tasks[0].State);
                    Assert.Equal(automatic, tasks[0].AutomaticRetryCount);
                    Assert.Equal(cycle, tasks[0].RetryCycle);
                    Assert.Equal(AgentReplyTaskState.Queued, tasks[1].State);
                    Assert.Equal(cycle * (quota + 1) + automatic + 1, await db.AgentTaskDeliveryAttempts.CountAsync());
                }
                if (automatic == quota) break;
                Assert.Equal(test.Clock.Now.AddSeconds(1), decision.NextRetryAt);
                test.Clock.Now = decision.NextRetryAt!.Value;
                var previous = current;
                current = Assert.IsType<AgentMentionResultDto>((await test.Run(s => s.WaitReliableAsync(new(connectionId), transport))).Data);
                Assert.Equal(previous.TaskId, current.TaskId);
                Assert.Equal(firstKey, current.ReplyIdempotencyKey);
                Assert.NotEqual(previous.DeliveryAttemptId, current.DeliveryAttemptId);
                await using (var scope = test.Services.CreateAsyncScope())
                {
                    var old = await scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>().AgentTaskDeliveryAttempts.SingleAsync(a => a.ID == previous.DeliveryAttemptId);
                    Assert.Equal(AgentTaskDeliveryAttemptState.Abandoned, old.State);
                    Assert.Equal(current.DeliveryAttemptId, old.ReplacementAttemptId);
                }
                Assert.True((await test.Run(s => s.ConfirmReliableAsync(new(connectionId, "task", TaskId: current.TaskId,
                    DeliveryAttemptId: current.DeliveryAttemptId), transport))).Succeeded);
            }
            if (cycle == 1) break;
            AgentReplyTask before;
            await using (var scope = test.Services.CreateAsyncScope())
                before = await scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>().AgentReplyTasks.SingleAsync(t => t.ID == current.TaskId);
            transport = RecoveryTransport(251);
            Assert.True((await test.Run(s => s.ResumeReliableAsync(new(connectionId, test.RecoveryKey, Guid.NewGuid()), transport))).Succeeded);
            Assert.Equal("retry_exhausted", (await test.Run(s => s.WaitReliableAsync(new(connectionId), transport))).ErrorCode);
            transport = RecoveryTransport(252);
            var takeover = Assert.IsType<AgentJoinResultDto>((await JoinReliableForTestAsync(test.Services,
                ReliableJoinRequest(test.MeetingCode, "Worker", "takeover"), transport)).Data);
            connectionId = takeover.Connection.ConnectionSessionId;
            Assert.True((await test.Run(s => s.ConfirmReliableAsync(new(connectionId, "initial", takeover.InitialDeliveryId), transport))).Succeeded);
            Assert.Equal("retry_exhausted", (await test.Run(s => s.WaitReliableAsync(new(connectionId), transport))).ErrorCode);
            await using (var scope = test.Services.CreateAsyncScope())
            {
                var after = await scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>().AgentReplyTasks.SingleAsync(t => t.ID == current.TaskId);
                Assert.Equal(before.FailedAtUtc, after.FailedAtUtc);
                Assert.Equal(before.FailureCode, after.FailureCode);
                Assert.Equal(before.RetryCycle, after.RetryCycle);
                Assert.Equal(before.AutomaticRetryCount, after.AutomaticRetryCount);
                Assert.Equal(before.NextRetryAtUtc, after.NextRetryAtUtc);
                Assert.True(after.RetryExhausted);
            }
            var retry = Assert.IsType<AgentRetryResultDto>((await test.Run(s => s.RetryReliableAsync(new(connectionId,
                current.TaskId, Guid.NewGuid()), transport))).Data);
            Assert.Equal(1, retry.RetryCycle);
            current = Assert.IsType<AgentMentionResultDto>((await test.Run(s => s.WaitReliableAsync(new(connectionId), transport))).Data);
            Assert.Equal(firstKey, current.ReplyIdempotencyKey);
            await using (var scope = test.Services.CreateAsyncScope())
                Assert.Equal(0, (await scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>().AgentReplyTasks.SingleAsync(t => t.ID == current.TaskId)).AutomaticRetryCount);
            Assert.True((await test.Run(s => s.ConfirmReliableAsync(new(connectionId, "task", TaskId: current.TaskId,
                DeliveryAttemptId: current.DeliveryAttemptId), transport))).Succeeded);
        }
    }
}
