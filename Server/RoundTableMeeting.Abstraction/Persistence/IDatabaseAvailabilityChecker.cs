namespace RoundTableMeeting.Abstraction.Persistence;

/// <summary>
/// 定义数据库可用性探测能力
/// </summary>
public interface IDatabaseAvailabilityChecker
{
    /// <summary>
    /// 探测数据库当前是否可访问
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}
