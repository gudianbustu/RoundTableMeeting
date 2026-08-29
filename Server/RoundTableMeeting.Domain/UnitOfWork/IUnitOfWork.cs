using Materal.TTA.Common;

namespace RoundTableMeeting.Domain.UnitOfWork;

public interface IUnitOfWork
{
    void RegisterAdd(IEntity<Guid> entity);

    void RegisterEdit(IEntity<Guid> entity);

    void RegisterDelete(IEntity<Guid> entity);

    Task<int> CommitAsync(CancellationToken cancellationToken = default);
}
