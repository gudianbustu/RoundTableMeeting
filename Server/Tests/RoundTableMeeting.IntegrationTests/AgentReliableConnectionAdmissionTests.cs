using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentReliableJoinsOnOneTransportCannotCreateOrTakeoverALosingIdentity(bool takeover)
    {
        using var factory = new TestApplicationFactory();
        using var browser = CreateBrowserClient(factory);
        await LoginAsync(browser, "alice", "test-password-alice");
        var firstMeeting = await CreateMeetingAsync(browser, "Concurrent transport A");
        var secondMeeting = await CreateMeetingAsync(browser, "Concurrent transport B");
        using var http = factory.CreateClient(new() { HandleCookies = false });
        await using var old = await CreateSdkAsync(http);
        await using var contender = await CreateSdkAsync(http);
        Guid oldId = Guid.Empty;
        if (takeover)
            oldId = (await ActivationCallAsync(old, "join_meeting", new { meetingCode = secondMeeting.MeetingCode,
                displayName = "Worker", contextCharacterLimit = 12000, reliabilityMode = "explicit_ack_v1",
                operationId = Guid.NewGuid(), recoveryKey = new string('A', 43) })).GetProperty("data")
                .GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
        var first = ActivationCallAsync(contender, "join_meeting", new { meetingCode = firstMeeting.MeetingCode,
            displayName = "Worker", contextCharacterLimit = 12000, reliabilityMode = "explicit_ack_v1",
            operationId = Guid.NewGuid(), recoveryKey = new string('A', 43) });
        var second = ActivationCallAsync(contender, "join_meeting", new { meetingCode = secondMeeting.MeetingCode,
            displayName = "Worker", contextCharacterLimit = 6000, reliabilityMode = "explicit_ack_v1",
            joinMode = takeover ? "takeover" : "create", operationId = Guid.NewGuid(), recoveryKey = new string('A', 43) });
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(results, r => r.GetProperty("succeeded").GetBoolean());
        Assert.Equal("connection_invalid", Assert.Single(results, r => !r.GetProperty("succeeded").GetBoolean()).GetProperty("errorCode").GetString());
        var firstWon = results[0].GetProperty("succeeded").GetBoolean();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        Assert.Equal(takeover ? 2 : 1, await db.AgentConnectionSessions.CountAsync());
        Assert.Equal(takeover ? 2 : 1, await db.AgentRecoveryOperations.CountAsync());
        Assert.Equal(firstWon ? 1 : 0, await db.AgentConnectionSessions.CountAsync(c => c.MeetingId == firstMeeting.MeetingId));
        if (takeover)
        {
            var previous = await db.AgentConnectionSessions.SingleAsync(c => c.ID == oldId);
            Assert.Equal(firstWon ? AgentConnectionState.Connected : AgentConnectionState.Superseded, previous.State);
            var originalStatus = await ActivationCallAsync(old, "get_agent_status", new { connectionSessionId = oldId });
            Assert.Equal(firstWon, originalStatus.GetProperty("succeeded").GetBoolean());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedReliableActivationPermanentlyPreventsLegacyDowngrade(bool upgradeLegacy)
    {
        using var original = new TestApplicationFactory();
        var registry = new ActivationFaultRegistry();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAgentConnectionRegistry>();
            services.AddSingleton<IAgentConnectionRegistry>(registry);
        }));
        using var browser = factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "No downgrade after activation failure");
        using var http = factory.CreateClient(new() { HandleCookies = false });
        await using var old = await CreateSdkAsync(http);
        await using var reliable = await CreateSdkAsync(http);
        await using var downgrade = await CreateSdkAsync(http);
        var legacy = new { meetingCode = meeting.MeetingCode, displayName = "Worker", contextCharacterLimit = 12000 };
        if (upgradeLegacy) Assert.True((await ActivationCallAsync(old, "join_meeting", legacy)).GetProperty("succeeded").GetBoolean());
        registry.Arm(0);
        var rejected = await ActivationCallAsync(reliable, "join_meeting", new { meetingCode = meeting.MeetingCode,
            displayName = "Worker", contextCharacterLimit = 12000, reliabilityMode = "explicit_ack_v1",
            joinMode = upgradeLegacy ? "takeover" : "create", operationId = Guid.NewGuid(), recoveryKey = new string('A', 43) });
        Assert.Equal("agent_operation_unavailable", rejected.GetProperty("errorCode").GetString());
        AssertToolFailure(await ActivationCallAsync(downgrade, "join_meeting", legacy), "reliability_mode_required");
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        var cursor = await db.AgentContextCursors.SingleAsync();
        Assert.Equal("explicit_ack_v1", cursor.RequiredReliabilityMode);
        Assert.Equal(AgentConnectionState.Disconnected, (await db.AgentConnectionSessions.SingleAsync(c => c.ID == cursor.CurrentConnectionId)).State);
        Assert.Equal(upgradeLegacy ? 2 : 1, await db.AgentConnectionSessions.CountAsync());
        Assert.Equal(1, await db.AgentRecoveryOperations.CountAsync());
    }
}
