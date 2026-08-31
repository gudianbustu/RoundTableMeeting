using RoundTableMeeting.Abstraction.Contracts.Authentication;

namespace RoundTableMeeting.Abstraction.Services;

/// <summary>
/// 定义幂等的启动账号播种能力
/// </summary>
public interface IAccountSeeder
{
    /// <summary>
    /// 确保指定账号存在且不覆盖已有账号
    /// </summary>
    Task SeedAsync(IReadOnlyCollection<SeedAccountInput> accounts, CancellationToken cancellationToken = default);
}
