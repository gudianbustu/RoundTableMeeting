using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Theory]
    [InlineData("pointer")]
    [InlineData("binding")]
    public async Task DurableOwnershipRevocationRejectsToolsWhileTransportRemainsRegistered(string changedFact)
    {
        using var factory = new TestApplicationFactory();
        using var browser = CreateBrowserClient(factory);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Ownership revocation");
        using var agent = CreateAnonymousClient(factory);
        var joined = await JoinLifecycleAgentAsync(agent, meeting.MeetingCode, "Worker");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            if (changedFact == "pointer")
                await db.Database.ExecuteSqlRawAsync("UPDATE AgentContextCursors SET CurrentConnectionId = NULL, ConnectionGeneration = 0", cancellationToken: TestContext.Current.CancellationToken);
            else
                await db.Database.ExecuteSqlRawAsync("UPDATE AgentConnectionSessions SET BindingVersion = BindingVersion + 1", cancellationToken: TestContext.Current.CancellationToken);
        }
        AssertToolError(await CallToolAsync(agent, joined.Session.TransportSessionId, 3, "get_agent_status",
            new { connectionSessionId = joined.ConnectionId }), "connection_invalid");
        AssertToolError(await CallToolAsync(agent, joined.Session.TransportSessionId, 4, "wait_for_mention",
            new { connectionSessionId = joined.ConnectionId }), "connection_invalid");
        AssertToolError(await CallToolAsync(agent, joined.Session.TransportSessionId, 5, "reply_to_mention",
            new { connectionSessionId = joined.ConnectionId, taskId = Guid.NewGuid(), deliveryAttemptId = Guid.NewGuid(),
                replyIdempotencyKey = Guid.NewGuid(), content = "Rejected reply" }), "connection_invalid");
        await using var readScope = factory.Services.CreateAsyncScope();
        var read = readScope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        Assert.Null((await read.AgentInitialContextDeliveries.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).ConfirmedAtUtc);
        Assert.Empty(await read.MeetingMessages.ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
    }
}
