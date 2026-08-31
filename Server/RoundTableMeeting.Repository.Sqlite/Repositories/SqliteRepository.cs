using Materal.TTA.Common;
using Materal.TTA.SqliteEFRepository;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.Repository.Sqlite.Repositories;

/// <summary>
/// 提供共用的 SQLite 仓储基类
/// </summary>
public abstract class SqliteRepository<TEntity, TPrimaryKeyType>(RoundTableMeetingDbContext dbContext)
    : SqliteEFRepositoryImpl<TEntity, TPrimaryKeyType, RoundTableMeetingDbContext>(dbContext)
    where TEntity : class, IEntity<TPrimaryKeyType>, new()
    where TPrimaryKeyType : struct
{
}
