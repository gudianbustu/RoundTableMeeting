using RoundTableMeeting.Domain.Entities;
using Materal.TTA.Common;

namespace RoundTableMeeting.Domain.Repositories;

/// <summary>
/// 定义用户账号的持久化查询
/// </summary>
public interface IUserAccountRepository : IRepository<UserAccount, Guid>
{
    /// <summary>
    /// 按规范化用户名和主键稳定列出全部账号。
    /// </summary>
    Task<IReadOnlyList<UserAccount>> ListAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 按规范化用户名查找账号
    /// </summary>
    Task<UserAccount?> FindByNormalizedUsernameAsync(string normalizedUsername, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按主键查找账号
    /// </summary>
    Task<UserAccount?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);
}
