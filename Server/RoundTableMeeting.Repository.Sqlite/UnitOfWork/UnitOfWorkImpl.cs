using Materal.Extensions.DependencyInjection;
using RoundTableMeeting.Domain.UnitOfWork;
using RoundTableMeeting.Repository.Sqlite.Database;
using TtaEntity = Materal.TTA.Common.IEntity<System.Guid>;
using ProjectUnitOfWork = RoundTableMeeting.Domain.UnitOfWork.IUnitOfWork;

namespace RoundTableMeeting.Repository.Sqlite.UnitOfWork;

public sealed class UnitOfWorkImpl(RoundTableMeetingDbContext dbContext)
    : ProjectUnitOfWork, IScopedDependency<ProjectUnitOfWork>
{
    private readonly EfUnitOfWorkCore<RoundTableMeetingDbContext> core = new(dbContext);

    public void RegisterAdd(TtaEntity entity) => core.RegisterAdd(entity);

    public void RegisterEdit(TtaEntity entity) => core.RegisterEdit(entity);

    public void RegisterDelete(TtaEntity entity) => core.RegisterDelete(entity);

    public Task<int> CommitAsync(CancellationToken cancellationToken = default) => core.CommitAsync(cancellationToken);
}
