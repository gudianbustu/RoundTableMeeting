using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Application.AgentCollaboration;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Fact]
    public async Task ResumeServiceReplaysOneVersionAndRejectsOldKeyBindingAndOperation()
    {
        using var factory = new TestApplicationFactory();
        using var browser = CreateBrowserClient(factory);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Resume service");
        var join = ReliableJoinRequest(meeting.MeetingCode, "Worker", "create");
        var first = RecoveryTransport(61);
        var second = RecoveryTransport(62);
        var third = RecoveryTransport(63);
        var joined = Assert.IsType<AgentJoinResultDto>((await JoinReliableForTestAsync(factory.Services, join, first)).Data);
        var request = new AgentResumeRequest(joined.Connection.ConnectionSessionId, join.RecoveryKey, Guid.NewGuid());
        Assert.Equal("resume_rejected", (await ReliableForTestAsync(factory.Services, service => service.ResumeReliableAsync(
            request with { RecoveryKey = ReliableJoinRequest(meeting.MeetingCode, "Unused", "create").RecoveryKey }, second))).ErrorCode);
        var result = Assert.IsType<AgentResumeResultDto>((await ReliableForTestAsync(factory.Services, service => service.ResumeReliableAsync(request, second))).Data);
        Assert.Equal(joined.Connection.ConnectionSessionId, result.Connection.ConnectionSessionId);
        Assert.Equal(1, result.Connection.ConnectionGeneration);
        Assert.Equal(2, result.Connection.BindingVersion);
        Assert.Equal(joined.InitialDeliveryId, result.InitialDeliveryId);
        var replay = Assert.IsType<AgentResumeResultDto>((await ReliableForTestAsync(factory.Services, service => service.ResumeReliableAsync(request, second))).Data);
        Assert.True(replay.Replayed);
        Assert.Equal(result.Connection.LeaseExpiresAt, replay.Connection.LeaseExpiresAt);
        Assert.Equal("connection_invalid", (await ReliableForTestAsync(factory.Services, service => service.RenewReliableAsync(new(request.ConnectionSessionId), first))).ErrorCode);
        Assert.Equal("operation_conflict", (await ReliableForTestAsync(factory.Services, service => service.ResumeReliableAsync(request, third))).ErrorCode);
        Assert.True((await ReliableForTestAsync(factory.Services, service => service.ResumeReliableAsync(request with { OperationId = Guid.NewGuid() }, third))).Succeeded);
        Assert.Equal("operation_superseded", (await ReliableForTestAsync(factory.Services, service => service.ResumeReliableAsync(request, second))).ErrorCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        Assert.Equal(1, await db.AgentConnectionSessions.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(3, (await db.AgentConnectionSessions.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).BindingVersion);
        Assert.Equal(3, await db.AgentRecoveryOperations.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InitialConfirmationReplayDoesNotRenewAndExpiredLeaseRequiresResume()
    {
        using var original = new TestApplicationFactory();
        var time = new RecoveryClock(DateTimeOffset.UtcNow);
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(time);
        }));
        using var browser = factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Explicit confirmation");
        await PostHumanMessageAsync(browser, meeting.MeetingId, "Initial history");
        var join = ReliableJoinRequest(meeting.MeetingCode, "Worker", "create");
        var transport = RecoveryTransport(64);
        var joined = Assert.IsType<AgentJoinResultDto>((await JoinReliableForTestAsync(factory.Services, join, transport)).Data);
        var confirm = new AgentConfirmContextRequest(joined.Connection.ConnectionSessionId, "initial", joined.InitialDeliveryId);
        time.Now = time.Now.AddSeconds(20);
        var confirmed = Assert.IsType<AgentConfirmContextResultDto>((await ReliableForTestAsync(factory.Services,
            service => service.ConfirmReliableAsync(confirm, transport))).Data);
        Assert.False(confirmed.Replayed);
        Assert.Equal(1, confirmed.DeliveredThroughSequence);
        var expires = time.Now.AddSeconds(120);
        time.Now = time.Now.AddSeconds(20);
        Assert.True(Assert.IsType<AgentConfirmContextResultDto>((await ReliableForTestAsync(factory.Services,
            service => service.ConfirmReliableAsync(confirm, transport))).Data).Replayed);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            Assert.Equal(expires, (await db.AgentConnectionSessions.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).LeaseExpiresAtUtc);
        }
        var renewed = Assert.IsType<AgentLeaseResultDto>((await ReliableForTestAsync(factory.Services,
            service => service.RenewReliableAsync(new(confirm.ConnectionSessionId), transport))).Data);
        Assert.Equal(time.Now.AddSeconds(120), renewed.LeaseExpiresAt);
        time.Now = renewed.LeaseExpiresAt;
        Assert.Equal("connection_expired", (await ReliableForTestAsync(factory.Services,
            service => service.RenewReliableAsync(new(confirm.ConnectionSessionId), transport))).ErrorCode);
        Assert.Equal("connection_expired", (await ReliableForTestAsync(factory.Services,
            service => service.ConfirmReliableAsync(confirm, transport))).ErrorCode);
        var resumed = Assert.IsType<AgentResumeResultDto>((await ReliableForTestAsync(factory.Services,
            service => service.ResumeReliableAsync(new(confirm.ConnectionSessionId, join.RecoveryKey, Guid.NewGuid()), RecoveryTransport(65)))).Data);
        Assert.Null(resumed.InitialContext);
        Assert.Equal("confirmed", resumed.Connection.InitialContextState);
    }

    private static McpTransportSessionKey RecoveryTransport(byte value) => McpTransportSessionKey.FromDigest(Enumerable.Repeat(value, 32).ToArray());
    private static async Task<AgentOperationResult> ReliableForTestAsync(IServiceProvider services, Func<AgentCollaborationService, Task<AgentOperationResult>> action)
    {
        await using var scope = services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AgentCollaborationService>());
    }
    private sealed class RecoveryClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
