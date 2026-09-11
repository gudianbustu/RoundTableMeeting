using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Fact]
    public async Task ExpiredTaskConfirmationUsesProductionErrorSchemaAndCannotAdvanceTheCursor()
    {
        await using var test = await SdkTaskBoundary.CreateAsync(1);
        var mention = await test.Call("wait_for_mention", new { connectionSessionId = test.Id });
        test.Clock.Now = mention.GetProperty("confirmationDeadline").GetDateTimeOffset();
        var args = new { connectionSessionId = test.Id, kind = "task", taskId = mention.GetProperty("taskId").GetGuid(),
            deliveryAttemptId = mention.GetProperty("deliveryAttemptId").GetGuid() };
        var before = await test.Facts();
        var error = await test.Call("confirm_context", args, "confirmation_expired");
        Assert.Equal("get_agent_status", error.GetProperty("nextAction").GetString());
        Assert.Equal(before, await test.Facts());
        await using (var scope = test.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            Assert.Equal(AgentReplyTaskState.Delivered, (await db.AgentReplyTasks.SingleAsync()).State);
            Assert.Null((await db.AgentTaskDeliveryAttempts.SingleAsync()).ConfirmedAtUtc);
            Assert.Equal(0, (await db.AgentContextCursors.SingleAsync()).DeliveredThroughSequence);
            Assert.Equal(AgentConnectionState.Connected, (await db.AgentConnectionSessions.SingleAsync()).State);
        }
        await test.RejectUnchanged("confirm_context", args, "confirmation_expired");
    }
}
