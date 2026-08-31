using Materal.TTA.Common;
using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Domain.Exceptions;

namespace RoundTableMeeting.Repository.Sqlite.UnitOfWork;

/// <summary>
/// 适配 EF Core 实体状态和工作单元操作
/// </summary>
internal sealed class EfUnitOfWorkCore<TDbContext>(TDbContext dbContext) where TDbContext : DbContext
{
    /// <summary>
    /// 登记实体新增操作
    /// </summary>
    public void RegisterAdd(IEntity<Guid> entity) => SetState(entity, Registration.Add);
    /// <summary>
    /// 登记实体修改操作
    /// </summary>
    public void RegisterEdit(IEntity<Guid> entity) => SetState(entity, Registration.Edit);
    /// <summary>
    /// 登记实体删除操作
    /// </summary>
    public void RegisterDelete(IEntity<Guid> entity) => SetState(entity, Registration.Delete);

    /// <summary>
    /// 提交工作单元中的实体变更
    /// </summary>
    public async Task<int> CommitAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new ConcurrencyConflictException("A concurrency conflict occurred while saving changes.", exception);
        }
        finally
        {
            // 成功或失败后清理跟踪状态，使作用域上下文可以复用
            dbContext.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// 根据登记操作设置 EF Core 实体状态
    /// </summary>
    private void SetState(IEntity<Guid> entity, Registration registration)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var existing = dbContext.ChangeTracker.Entries()
            .Select(entry => entry.Entity)
            .OfType<IEntity<Guid>>()
            .FirstOrDefault(tracked => tracked.GetType() == entity.GetType()
                && tracked.ID == entity.ID
                && !ReferenceEquals(tracked, entity));
        if (existing is not null)
        {
            throw new InvalidOperationException("An entity with the same type and ID is already tracked.");
        }

        var entry = dbContext.Entry(entity);
        switch (registration, entry.State)
        {
            case (_, EntityState.Detached):
                entry.State = registration switch
                {
                    Registration.Add => EntityState.Added,
                    Registration.Edit => EntityState.Modified,
                    _ => EntityState.Deleted
                };
                return;
            case (Registration.Add, EntityState.Added):
                return;
            case (Registration.Edit, EntityState.Added):
                return;
            case (Registration.Delete, EntityState.Added):
                entry.State = EntityState.Detached;
                return;
            case (Registration.Add, EntityState.Unchanged or EntityState.Modified or EntityState.Deleted):
                throw new InvalidOperationException("The entity state does not allow adding this entity.");
            case (Registration.Edit, EntityState.Unchanged):
                entry.State = EntityState.Modified;
                return;
            case (Registration.Delete, EntityState.Unchanged):
                entry.State = EntityState.Deleted;
                return;
            case (Registration.Edit, EntityState.Modified):
                return;
            case (Registration.Delete, EntityState.Modified):
                entry.State = EntityState.Deleted;
                return;
            case (Registration.Delete, EntityState.Deleted):
                return;
            case (Registration.Edit or Registration.Add, EntityState.Deleted):
                throw new InvalidOperationException("The entity state does not allow this registration.");
            default:
                throw new InvalidOperationException("The entity state does not allow this registration.");
        }
    }

    /// <summary>
    /// 定义实体登记对应的持久化操作
    /// </summary>
    private enum Registration
    {
        Add,
        Edit,
        Delete
    }
}
