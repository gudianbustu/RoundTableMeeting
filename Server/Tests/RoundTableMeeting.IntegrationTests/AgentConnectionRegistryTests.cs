using System;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.AgentCollaboration;
using Xunit;

namespace RoundTableMeeting.IntegrationTests;

public sealed class AgentConnectionRegistryTests
{
	[Fact]
	public void RegistrationRequiresUniqueTransportAndMeetingParticipantBinding()
	{
		AgentConnectionRegistry agentConnectionRegistry = new AgentConnectionRegistry();
		Guid meetingId = Guid.NewGuid();
		Guid participantId = Guid.NewGuid();
		McpTransportSessionKey transportSession = Session(1);
		AgentConnectionRegistration agentConnectionRegistration = new AgentConnectionRegistration(meetingId, participantId, Guid.NewGuid(), "binding-1");
		Assert.True(agentConnectionRegistry.TryRegister(transportSession, agentConnectionRegistration));
		Assert.True(agentConnectionRegistry.ContainsTransport(transportSession));
		Assert.True(agentConnectionRegistry.IsRegistered(meetingId, participantId, agentConnectionRegistration.ConnectionSessionId, agentConnectionRegistration.TransportBinding));
		Assert.False(agentConnectionRegistry.TryRegister(transportSession, agentConnectionRegistration with
		{
			ConnectionSessionId = Guid.NewGuid()
		}));
		Assert.False(agentConnectionRegistry.TryRegister(Session(2), agentConnectionRegistration with
		{
			ConnectionSessionId = Guid.NewGuid(),
			TransportBinding = "binding-2"
		}));
		Assert.True(agentConnectionRegistry.TryRegister(Session(3), new AgentConnectionRegistration(Guid.NewGuid(), participantId, Guid.NewGuid(), "binding-3")));
	}

	[Fact]
	public void UnregisterOnlyRemovesTheExactConnection()
	{
		AgentConnectionRegistry agentConnectionRegistry = new AgentConnectionRegistry();
		McpTransportSessionKey transportSession = Session(1);
		AgentConnectionRegistration agentConnectionRegistration = new AgentConnectionRegistration(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "binding");
		Assert.True(agentConnectionRegistry.TryRegister(transportSession, agentConnectionRegistration));
		Assert.False(agentConnectionRegistry.Unregister(transportSession, Guid.NewGuid()));
		Assert.True(agentConnectionRegistry.ContainsTransport(transportSession));
		Assert.True(agentConnectionRegistry.Unregister(transportSession, agentConnectionRegistration.ConnectionSessionId));
		Assert.False(agentConnectionRegistry.ContainsTransport(transportSession));
		Assert.False(agentConnectionRegistry.Unregister(transportSession, agentConnectionRegistration.ConnectionSessionId));
	}

	[Fact]
	public void RegistrationsCanBeListedByMeetingAndRemovedByTransportSession()
	{
		AgentConnectionRegistry registry = new AgentConnectionRegistry();
		Guid meetingId = Guid.NewGuid();
		McpTransportSessionKey firstSession = Session(1);
		McpTransportSessionKey secondSession = Session(2);
		AgentConnectionRegistration first = new(meetingId, Guid.NewGuid(), Guid.NewGuid(), "binding-1");
		AgentConnectionRegistration second = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "binding-2");
		Assert.True(registry.TryRegister(firstSession, first));
		Assert.True(registry.TryRegister(secondSession, second));

		Assert.Equal(first, Assert.Single(registry.ListRegistrations(meetingId)));
		Assert.Equal(first, Assert.Single(registry.RemoveRegistrations(firstSession)));
		Assert.False(registry.ContainsTransport(firstSession));
		Assert.True(registry.ContainsTransport(secondSession));
		Assert.Empty(registry.RemoveRegistrations(firstSession));
	}

	private static McpTransportSessionKey Session(byte seed)
	{
		Span<byte> span = stackalloc byte[32];
		span.Fill(seed);
		return McpTransportSessionKey.FromDigest(span);
	}
}
