using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Domain.AgentCollaboration;
using RoundTableMeeting.Application.AgentCollaboration;
using RoundTableMeeting.Domain.Enums;

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
                result = await scope.ServiceProvider.GetRequiredService<AgentCollaborationService>()
                    .ReconcileConnectionAsync(registration, AgentTaskFailureCode.TransportClosed, cancellationToken);
			}
			return result;
		}
		catch (Exception ex) when (!(ex is OperationCanceledException))
		{
			logger.LogError("Failed to compensate Agent connection {ConnectionSessionId} in meeting {MeetingId}. ExceptionType: {ExceptionType}", registration.ConnectionSessionId, registration.MeetingId, ex.GetType().Name);
			return false;
		}
	}
}
