using RoundTableMeeting.Abstraction.Contracts;

namespace RoundTableMeeting.Abstraction.Services;

public interface ISystemStatusService
{
    Task<SystemStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);
}
