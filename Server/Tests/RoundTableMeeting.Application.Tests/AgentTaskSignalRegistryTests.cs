using System;
using System.Threading;
using System.Threading.Tasks;
using RoundTableMeeting.Application.AgentCollaboration;
using Xunit;

namespace RoundTableMeeting.Application.Tests;

public sealed class AgentTaskSignalRegistryTests
{
    [Fact]
    public async Task MeetingTerminationWakesEveryConcurrentWaiterAndPreservesOtherMeetings()
    {
        var registry = new AgentTaskSignalRegistry(new AgentCollaborationOptions());
        var meeting = Guid.NewGuid();
        var participant = Guid.NewGuid();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = registry.WaitAsync(meeting, participant, TimeSpan.FromMinutes(5), timeout.Token);
        var concurrent = registry.WaitAsync(meeting, participant, TimeSpan.FromMinutes(5), timeout.Token);
        var second = registry.WaitAsync(meeting, Guid.NewGuid(), TimeSpan.FromMinutes(5), timeout.Token);
        var independent = registry.WaitAsync(Guid.NewGuid(), participant, TimeSpan.FromMilliseconds(100), timeout.Token);
        registry.TerminateMeeting(meeting);
        Assert.True(await first);
        Assert.True(await concurrent);
        Assert.True(await second);
        Assert.False(await independent);
        registry.TerminateMeeting(meeting);
    }

	[Fact]
	public async Task SignalWakesTheMatchingAgentOnly()
	{
		AgentTaskSignalRegistry registry = new AgentTaskSignalRegistry(new AgentCollaborationOptions
		{
			WakeSignalCapacity = 2
		});
		Guid meetingId = Guid.NewGuid();
		Guid firstAgentId = Guid.NewGuid();
		Guid secondAgentId = Guid.NewGuid();
		using CancellationTokenSource cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2L));
		Task<bool> firstWait = registry.WaitAsync(meetingId, firstAgentId, TimeSpan.FromSeconds(1L), cancellation.Token);
		Task<bool> secondWait = registry.WaitAsync(meetingId, secondAgentId, TimeSpan.FromMilliseconds(50L), cancellation.Token);
		registry.Signal(meetingId, firstAgentId);
		Assert.True(await firstWait);
		Assert.False(await secondWait);
	}

	[Fact]
	public async Task SaturatedSignalCapacityDoesNotLoseDatabaseFallbackSemantics()
	{
		AgentTaskSignalRegistry registry = new AgentTaskSignalRegistry(new AgentCollaborationOptions
		{
			WakeSignalCapacity = 1
		});
		Guid meetingId = Guid.NewGuid();
		Guid agentId = Guid.NewGuid();
		registry.Signal(meetingId, agentId);
		registry.Signal(meetingId, agentId);
		Assert.True(await registry.WaitAsync(meetingId, agentId, TimeSpan.FromSeconds(1L)));
	}
}
