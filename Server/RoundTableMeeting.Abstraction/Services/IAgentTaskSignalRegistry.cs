using System;
using System.Threading;
using System.Threading.Tasks;

namespace RoundTableMeeting.Abstraction.Services;

public interface IAgentTaskSignalRegistry
{
    void TerminateMeeting(Guid meetingId);

	void Signal(Guid meetingId, Guid agentParticipantId);

	Task<bool> WaitAsync(Guid meetingId, Guid agentParticipantId, TimeSpan timeout, CancellationToken cancellationToken = default);
}
