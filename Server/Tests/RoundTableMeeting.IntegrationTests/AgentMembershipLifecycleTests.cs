using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;
using RoundTableMeeting.Abstraction.Contracts.Meetings;
using RoundTableMeeting.Abstraction.Enums;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;
namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Fact]
    public async Task AgentLeaveBlocksResumeAndTakeoverUntilExplicitJoinAndOldProofSurvives()
    {
        using var factory = new TestApplicationFactory();
        using var browser = CreateBrowserClient(factory);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Agent membership");
        var firstTransport = McpTransportSessionKey.FromDigest(Enumerable.Repeat((byte)131, 32).ToArray());
        var firstRequest = ReliableJoinRequest(meeting.MeetingCode, "Worker", "create");
        var first = Assert.IsType<AgentJoinResultDto>((await JoinReliableForTestAsync(factory.Services, firstRequest, firstTransport)).Data);
        Assert.NotNull(first.LifecycleProof);
        var nextTransport = McpTransportSessionKey.FromDigest(Enumerable.Repeat((byte)132, 32).ToArray());
        var takeoverRequest = ReliableJoinRequest(meeting.MeetingCode, "Worker", "takeover");
        var takeover = Assert.IsType<AgentJoinResultDto>((await JoinReliableForTestAsync(factory.Services, takeoverRequest, nextTransport)).Data);
        var leaveOperation = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<IMeetingMembershipService>().LeaveAsync(meeting.MeetingId, takeover.Agent.ParticipantId, leaveOperation, TestContext.Current.CancellationToken);
            Assert.Equal(MeetingOperationStatus.Success, result.Status);
            Assert.Equal("active", (await scope.ServiceProvider.GetRequiredService<IMeetingLifecycleService>().GetStatusAsync(new(meeting.MeetingId, first.LifecycleProof!), TestContext.Current.CancellationToken))!.Status);
        }
        Assert.Empty(factory.Services.GetRequiredService<IAgentConnectionRegistry>().ListRegistrations(meeting.MeetingId));
        var thirdTransport = McpTransportSessionKey.FromDigest(Enumerable.Repeat((byte)133, 32).ToArray());
        var resumed = await ReliableForTestAsync(factory.Services, service => service.ResumeReliableAsync(
            new(takeover.Connection.ConnectionSessionId, takeoverRequest.RecoveryKey, Guid.NewGuid()), thirdTransport));
        Assert.Equal("membership_left", resumed.ErrorCode);
        var deniedTakeover = await JoinReliableForTestAsync(factory.Services, ReliableJoinRequest(meeting.MeetingCode, "Worker", "takeover"), thirdTransport);
        Assert.Equal("membership_left", deniedTakeover.ErrorCode);
        await PostHumanMessageAsync(browser, meeting.MeetingId, "During absence");
        var rejoinedResult = await JoinReliableForTestAsync(factory.Services, ReliableJoinRequest(meeting.MeetingCode, "Worker", "create"), thirdTransport);
        Assert.True(rejoinedResult.Succeeded, rejoinedResult.ErrorCode);
        var rejoined = Assert.IsType<AgentJoinResultDto>(rejoinedResult.Data);
        Assert.Equal(first.Agent.ParticipantId, rejoined.Agent.ParticipantId);
        Assert.True(rejoined.Connection.ConnectionGeneration > takeover.Connection.ConnectionGeneration);
        await using var final = factory.Services.CreateAsyncScope();
        await final.ServiceProvider.GetRequiredService<IMeetingMembershipService>().LeaveAsync(meeting.MeetingId, rejoined.Agent.ParticipantId, leaveOperation, TestContext.Current.CancellationToken);
        var db = final.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        Assert.Equal(MeetingMembershipState.Joined, (await db.MeetingParticipants.SingleAsync(p => p.ID == first.Agent.ParticipantId, cancellationToken: TestContext.Current.CancellationToken)).MembershipState);
        Assert.True(factory.Services.GetRequiredService<IAgentConnectionRegistry>().ContainsTransport(thirdTransport));
        await DeleteLifecycleMeetingAsync(browser, meeting.MeetingId, "Agent membership");
        await AssertAgentMeetingGoneAsync(factory, meeting.MeetingId);
        Assert.Equal("deleted", (await final.ServiceProvider.GetRequiredService<IMeetingLifecycleService>()
            .GetStatusAsync(new(meeting.MeetingId, first.LifecycleProof!), TestContext.Current.CancellationToken))!.Status);
    }
}
