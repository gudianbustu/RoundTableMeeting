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
    [Fact]
    public async Task RetiringBindingEndsActualSdkWaitBeforeSessionOrDatabaseDisconnect()
    {
        using var original = new TestApplicationFactory();
        var signals = new ObservedTaskSignals();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAgentTaskSignalRegistry>();
            services.AddSingleton<IAgentTaskSignalRegistry>(signals);
        }));
        using var browser = factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Binding retirement");
        using var http = factory.CreateClient(new() { HandleCookies = false });
        await using var sdk = await CreateSdkAsync(http);
        var joined = SdkPayload(await sdk.CallToolAsync("join_meeting", new Dictionary<string, object?>
        {
            ["meetingCode"] = meeting.MeetingCode, ["displayName"] = "SdkWorker", ["contextCharacterLimit"] = 12000
        }, cancellationToken: TestContext.Current.CancellationToken));
        var connectionId = joined.GetProperty("data").GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
        var args = new Dictionary<string, object?> { ["connectionSessionId"] = connectionId };
        var wait = sdk.CallToolAsync("wait_for_mention", args, cancellationToken: TestContext.Current.CancellationToken).AsTask();
        await signals.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var registry = factory.Services.GetRequiredService<IAgentConnectionRegistry>();
        var registration = Assert.Single(registry.ListRegistrations(meeting.MeetingId));
        Assert.True(registry.Retire(registration));
        await signals.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        AssertToolFailure(SdkPayload(await wait.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)), "connection_invalid");
        AssertToolFailure(SdkPayload(await sdk.CallToolAsync("get_agent_status", args, cancellationToken: TestContext.Current.CancellationToken)), "connection_invalid");
        AssertToolFailure(SdkPayload(await sdk.CallToolAsync("wait_for_mention", args, cancellationToken: TestContext.Current.CancellationToken)), "connection_invalid");
        // This intentionally isolates the registration-to-compensation gap: no database disconnect was requested.
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        Assert.Equal(AgentConnectionState.Connected, (await db.AgentConnectionSessions.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).State);
        Assert.Empty(await db.AgentReplyTasks.ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
    }
}
