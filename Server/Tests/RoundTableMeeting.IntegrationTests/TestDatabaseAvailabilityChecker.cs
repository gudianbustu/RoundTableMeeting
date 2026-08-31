using Microsoft.Extensions.Logging;
using RoundTableMeeting.Abstraction.Persistence;

namespace RoundTableMeeting.IntegrationTests;

/// <summary>
/// 提供数据库不可用的测试替身
/// </summary>
public sealed class TestDatabaseAvailabilityChecker(
    ILogger<TestDatabaseAvailabilityChecker> logger) : IDatabaseAvailabilityChecker
{
    /// <summary>
    /// 返回数据库可用性探测结果
    /// </summary>
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        logger.LogWarning("Test database availability check failed.");
        return Task.FromResult(false);
    }
}
