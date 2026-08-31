using Microsoft.EntityFrameworkCore;
using Materal.Extensions.DependencyInjection;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.Repository.Sqlite.Repositories;

/// <summary>
/// 提供用户账号的 SQLite 查询能力
/// </summary>
public sealed class UserAccountRepository : SqliteRepository<UserAccount, Guid>, IUserAccountRepository, IScopedDependency<IUserAccountRepository>
{
    /// <summary>
    /// 用于数据库操作的 EF Core 上下文
    /// </summary>
    private readonly RoundTableMeetingDbContext dbContext;

    /// <summary>
    /// 使用作用域内 EF Core 上下文初始化用户仓储
    /// </summary>
    public UserAccountRepository(RoundTableMeetingDbContext dbContext)
        : base(dbContext)
    {
        this.dbContext = dbContext;
    }

    /// <summary>
    /// 按规范化用户名查询账号
    /// </summary>
    public Task<UserAccount?> FindByNormalizedUsernameAsync(string normalizedUsername, CancellationToken cancellationToken = default)
        => dbContext.UserAccounts.SingleOrDefaultAsync(account => account.NormalizedUsername == normalizedUsername, cancellationToken);

    /// <summary>
    /// 按用户标识查询账号
    /// </summary>
    public Task<UserAccount?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => dbContext.UserAccounts.SingleOrDefaultAsync(account => account.ID == id, cancellationToken);
}
