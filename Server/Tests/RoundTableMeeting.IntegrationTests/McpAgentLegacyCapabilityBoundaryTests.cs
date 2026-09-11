using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Fact]
    public async Task EveryReliableToolRejectsABoundLegacyAgentWithoutMutationAndAnonymousResumeDoesNotRevealMode()
    {
        using var factory = new TestApplicationFactory();
        using var browser = CreateBrowserClient(factory);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Legacy capability boundary");
        using var http = factory.CreateClient(new() { HandleCookies = false });
        await using var legacy = await CreateSdkAsync(http);
        await using var anonymous = await CreateSdkAsync(http);
        var joined = (await ActivationCallAsync(legacy, "join_meeting", new { meetingCode = meeting.MeetingCode,
            displayName = "Worker", contextCharacterLimit = 12000 })).GetProperty("data");
        var id = joined.GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
        var key = new string('A', 43);
        var resume = new { connectionSessionId = id, recoveryKey = key, operationId = Guid.NewGuid() };
        async Task<string> Facts()
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            return JsonSerializer.Serialize(new { Connections = await db.AgentConnectionSessions.ToArrayAsync(),
                Initial = await db.AgentInitialContextDeliveries.ToArrayAsync(), Cursor = await db.AgentContextCursors.SingleAsync(),
                Tasks = await db.AgentReplyTasks.CountAsync(), Receipts = await db.AgentRecoveryOperations.CountAsync() });
        }
        var before = await Facts();
        var calls = new (string Name, object Args)[]
        {
            ("confirm_context", new { connectionSessionId = id, kind = "initial", initialDeliveryId = Guid.NewGuid() }),
            ("renew_agent_lease", new { connectionSessionId = id }),
            ("report_task_failure", new { connectionSessionId = id, taskId = Guid.NewGuid(), deliveryAttemptId = Guid.NewGuid(), failureCode = "agent_permanent_failure" }),
            ("retry_task", new { connectionSessionId = id, taskId = Guid.NewGuid(), operationId = Guid.NewGuid() }),
            ("resume_agent", resume)
        };
        foreach (var (name, args) in calls)
        {
            AssertToolFailure(await ActivationCallAsync(legacy, name, args), "reliability_mode_required");
            Assert.Equal(before, await Facts());
        }
        foreach (var target in new[] { id, Guid.NewGuid() })
        {
            var rejected = await ActivationCallAsync(anonymous, "resume_agent", resume with { connectionSessionId = target });
            Assert.Equal("resume_rejected", rejected.GetProperty("errorCode").GetString());
            Assert.Equal("stop", rejected.GetProperty("data").GetProperty("nextAction").GetString());
            Assert.Equal(before, await Facts());
        }
    }
}
