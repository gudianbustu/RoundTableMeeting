using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;
using RoundTableMeeting.Application.AgentCollaboration;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Theory]
    [InlineData("source_attempts")]
    [InlineData("connection_attempts")]
    [InlineData("source_failures")]
    [InlineData("connection_failures")]
    public async Task ProductionResumeRateLimitsSurviveNewSessionsAndOperationsBeforeOwnershipChanges(string dimension)
    {
        using var test = await ReliableRaceFixture.CreateAsync();
        var options = test.Services.GetRequiredService<AgentCollaborationOptions>();
        if (dimension == "source_attempts") options.ResumeRateLimitPerSource = 2;
        if (dimension == "connection_attempts") options.ResumeRateLimitPerConnection = 2;
        if (dimension == "source_failures") options.ResumeFailureLimitPerSource = 2;
        if (dimension == "connection_failures") options.ResumeFailureLimitPerConnection = 2;
        using var http = test.Factory.CreateClient(new() { HandleCookies = false });
        await using var first = await CreateSdkAsync(http);
        await using var second = await CreateSdkAsync(http);
        await using var third = await CreateSdkAsync(http);
        var wrongKey = new string('B', 42) + "A";
        var firstId = dimension.StartsWith("source", StringComparison.Ordinal) ? Guid.NewGuid() : test.ConnectionId;
        var denied = await ActivationCallAsync(first, "resume_agent", new { connectionSessionId = firstId,
            recoveryKey = wrongKey, operationId = Guid.NewGuid() });
        Assert.Equal("resume_rejected", denied.GetProperty("errorCode").GetString());
        denied = await ActivationCallAsync(second, "resume_agent", new { connectionSessionId = test.ConnectionId,
            recoveryKey = wrongKey, operationId = Guid.NewGuid() });
        Assert.Equal("resume_rejected", denied.GetProperty("errorCode").GetString());
        var blocked = await ActivationCallAsync(third, "resume_agent", new { connectionSessionId = test.ConnectionId,
            recoveryKey = test.RecoveryKey, operationId = Guid.NewGuid() });
        Assert.Equal("agent_operation_unavailable", blocked.GetProperty("errorCode").GetString());
        Assert.Equal(300, blocked.GetProperty("data").GetProperty("retryAfterSeconds").GetInt32());
        Assert.Equal("stop", blocked.GetProperty("data").GetProperty("nextAction").GetString());
        Assert.DoesNotContain(test.RecoveryKey, blocked.GetRawText());
        await using (var scope = test.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            var connection = await db.AgentConnectionSessions.SingleAsync();
            Assert.Equal(1, connection.BindingVersion);
            Assert.Equal(AgentConnectionState.Connected, connection.State);
            Assert.Equal(AgentReplyTaskState.Processing, (await db.AgentReplyTasks.SingleAsync()).State);
            Assert.Equal(1, await db.AgentRecoveryOperations.CountAsync());
            Assert.Equal(1, await db.AgentTaskDeliveryAttempts.CountAsync());
        }
        // A rejected admission reserves no transport and creates no receipt. Once the
        // actual server window expires, the same transport can make a fresh resume.
        test.Clock.Now = test.Clock.Now.AddSeconds(300);
        var resumed = await ActivationCallAsync(third, "resume_agent", new { connectionSessionId = test.ConnectionId,
            recoveryKey = test.RecoveryKey, operationId = Guid.NewGuid() });
        Assert.True(resumed.GetProperty("succeeded").GetBoolean(), resumed.GetProperty("errorCode").GetString());
        Assert.Equal(2, resumed.GetProperty("data").GetProperty("connection").GetProperty("bindingVersion").GetInt64());
    }

    [Fact]
    public async Task ProductionCreateAndTakeoverShareJoinQuotaAndRejectedTakeoverDoesNotDisplaceCurrentAgent()
    {
        using var test = await ReliableRaceFixture.CreateAsync();
        test.Services.GetRequiredService<AgentCollaborationOptions>().JoinRateLimitPerSource = 1;
        using var http = test.Factory.CreateClient(new() { HandleCookies = false });
        await using var first = await CreateSdkAsync(http);
        await using var second = await CreateSdkAsync(http);
        var key = new string('A', 43);
        foreach (var (sdk, mode) in new[] { (first, "create"), (second, "takeover") })
        {
            var blocked = await ActivationCallAsync(sdk, "join_meeting", new { meetingCode = test.MeetingCode,
                displayName = "Worker", contextCharacterLimit = 6000, reliabilityMode = "explicit_ack_v1",
                joinMode = mode, operationId = Guid.NewGuid(), recoveryKey = key });
            Assert.Equal("agent_operation_unavailable", blocked.GetProperty("errorCode").GetString());
            Assert.Equal(300, blocked.GetProperty("data").GetProperty("retryAfterSeconds").GetInt32());
        }
        Assert.True((await test.Run(s => s.RenewReliableAsync(new(test.ConnectionId), test.Transport))).Succeeded);
        await using (var scope = test.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            Assert.Equal(test.ConnectionId, (await db.AgentContextCursors.SingleAsync()).CurrentConnectionId);
            Assert.Equal(1, await db.AgentConnectionSessions.CountAsync());
            Assert.Equal(AgentReplyTaskState.Processing, (await db.AgentReplyTasks.SingleAsync()).State);
        }
        test.Clock.Now = test.Clock.Now.AddSeconds(300);
        var taken = await ActivationCallAsync(second, "join_meeting", new { meetingCode = test.MeetingCode,
            displayName = "Worker", contextCharacterLimit = 6000, reliabilityMode = "explicit_ack_v1",
            joinMode = "takeover", operationId = Guid.NewGuid(), recoveryKey = key });
        Assert.True(taken.GetProperty("succeeded").GetBoolean(), taken.GetProperty("errorCode").GetString());
        Assert.NotEqual(test.ConnectionId, taken.GetProperty("data").GetProperty("connection").GetProperty("connectionSessionId").GetGuid());
    }
}
