using System;
using System.Threading;
using System.Threading.Tasks;

namespace RoundTableMeeting.Abstraction.Services;

public interface IAgentJoinCoordinator
{
	ValueTask<IAsyncDisposable> AcquireAsync(Guid meetingId, string normalizedDisplayName, CancellationToken cancellationToken = default);
}
