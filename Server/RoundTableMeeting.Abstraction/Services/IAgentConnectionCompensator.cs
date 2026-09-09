using System;
using System.Threading;
using System.Threading.Tasks;

namespace RoundTableMeeting.Abstraction.Services;

public interface IAgentConnectionCompensator
{
	Task<bool> DisconnectAsync(AgentConnectionRegistration registration, DateTimeOffset disconnectedAtUtc, CancellationToken cancellationToken = default);
}
