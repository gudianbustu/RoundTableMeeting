using Materal.Extensions.DependencyInjection;
using RoundTableMeeting.Abstraction.Contracts;
using RoundTableMeeting.Abstraction.Persistence;
using RoundTableMeeting.Abstraction.Runtime;
using RoundTableMeeting.Abstraction.Services;

namespace RoundTableMeeting.Application.SystemStatus;

public sealed class SystemStatusService(
    IDatabaseAvailabilityChecker databaseAvailabilityChecker,
    IRuntimeEnvironment runtimeEnvironment)
    : ISystemStatusService, IScopedDependency<ISystemStatusService>
{
    public async Task<SystemStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var databaseAvailable = await databaseAvailabilityChecker.IsAvailableAsync(cancellationToken);
        return new SystemStatusDto(
            databaseAvailable ? "available" : "degraded",
            new DatabaseStatusDto(databaseAvailable ? "available" : "unavailable"),
            runtimeEnvironment.EnvironmentName);
    }
}
