using Materal.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Domain.Exceptions;
using RoundTableMeeting.Domain.UnitOfWork;
using RoundTableMeeting.Repository.Sqlite.Database;
using TtaEntity = Materal.TTA.Common.IEntity<System.Guid>;
using ProjectUnitOfWork = RoundTableMeeting.Domain.UnitOfWork.IUnitOfWork;

namespace RoundTableMeeting.Repository.Sqlite.UnitOfWork;

/// <summary>
/// 提供面向应用层的 EF Core 工作单元
/// </summary>
public sealed class UnitOfWorkImpl(RoundTableMeetingDbContext dbContext)
    : ProjectUnitOfWork, IScopedDependency<ProjectUnitOfWork>
{
    /// <summary>
    /// 负责执行 EF Core 实体状态转换的核心工作单元
    /// </summary>
    private readonly EfUnitOfWorkCore<RoundTableMeetingDbContext> core = new(dbContext);

    /// <summary>
    /// 登记实体新增操作
    /// </summary>
    public void RegisterAdd(TtaEntity entity) => core.RegisterAdd(entity);

    /// <summary>
    /// 登记实体修改操作
    /// </summary>
    public void RegisterEdit(TtaEntity entity) => core.RegisterEdit(entity);

    /// <summary>
    /// 登记实体删除操作
    /// </summary>
    public void RegisterDelete(TtaEntity entity) => core.RegisterDelete(entity);

    /// <summary>
    /// 提交工作单元中的实体变更
    /// </summary>
    public async Task<int> CommitAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await core.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUsernameUniqueConflict(exception))
        {
            throw new UsernameConflictException("The username is already in use.", exception);
        }
    }

    private static bool IsUsernameUniqueConflict(DbUpdateException exception)
        => exception.InnerException is SqliteException sqlite
            && sqlite.SqliteErrorCode == 19
            && (sqlite.Message.Contains("IX_UserAccounts_NormalizedUsername", StringComparison.Ordinal)
                || sqlite.Message.Contains("UserAccounts.NormalizedUsername", StringComparison.Ordinal));
}
