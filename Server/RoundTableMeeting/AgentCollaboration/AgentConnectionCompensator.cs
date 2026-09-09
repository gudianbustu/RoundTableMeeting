using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Repositories;

namespace RoundTableMeeting.AgentCollaboration;

public sealed class AgentConnectionCompensator(IServiceScopeFactory scopeFactory, ILogger<AgentConnectionCompensator> logger) : IAgentConnectionCompensator
{
	public async Task<bool> DisconnectAsync(AgentConnectionRegistration registration, DateTimeOffset disconnectedAtUtc, CancellationToken cancellationToken = default(CancellationToken))
	{
		try
		{
			bool result;
			await using (AsyncServiceScope scope = scopeFactory.CreateAsyncScope())
			{
				IAgentCollaborationRepository repository = scope.ServiceProvider.GetRequiredService<IAgentCollaborationRepository>();
				result = await repository.DisconnectConnectionAsync(registration.MeetingId, registration.ParticipantId, registration.ConnectionSessionId, registration.TransportBinding, disconnectedAtUtc, cancellationToken);
			}
			return result;
		}
		catch (Exception ex) when (!(ex is OperationCanceledException))
		{
			logger.LogError(ex, "Failed to compensate Agent connection {ConnectionSessionId} in meeting {MeetingId}.", registration.ConnectionSessionId, registration.MeetingId);
			return false;
		}
	}
}
