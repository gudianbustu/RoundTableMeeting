using Materal.Extensions.DependencyInjection;
using RoundTableMeeting.Abstraction.Contracts;
using RoundTableMeeting.Abstraction.Persistence;
using RoundTableMeeting.Abstraction.Runtime;
using RoundTableMeeting.Abstraction.Services;

namespace RoundTableMeeting.Application.SystemStatus;

/// <summary>
/// 组合数据库探测结果和宿主环境名称
/// </summary>
public sealed class SystemStatusService(
    IDatabaseAvailabilityChecker databaseAvailabilityChecker,
    IRuntimeEnvironment runtimeEnvironment)
    : ISystemStatusService, IScopedDependency<ISystemStatusService>
{
    /// <summary>
    /// 返回当前系统可用性视图
    /// </summary>
    public async Task<SystemStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var databaseAvailable = await databaseAvailabilityChecker.IsAvailableAsync(cancellationToken);
        return new SystemStatusDto(
            databaseAvailable ? "available" : "degraded",
            new DatabaseStatusDto(databaseAvailable ? "available" : "unavailable"),
            runtimeEnvironment.EnvironmentName);
    }
}
