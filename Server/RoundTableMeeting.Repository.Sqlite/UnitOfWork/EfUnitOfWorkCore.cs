using Materal.TTA.Common;
using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Domain.Exceptions;

namespace RoundTableMeeting.Repository.Sqlite.UnitOfWork;

internal sealed class EfUnitOfWorkCore<TDbContext>(TDbContext dbContext) where TDbContext : DbContext
{
    public void RegisterAdd(IEntity<Guid> entity) => SetState(entity, Registration.Add);
    public void RegisterEdit(IEntity<Guid> entity) => SetState(entity, Registration.Edit);
    public void RegisterDelete(IEntity<Guid> entity) => SetState(entity, Registration.Delete);

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
            dbContext.ChangeTracker.Clear();
        }
    }

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

    private enum Registration
    {
        Add,
        Edit,
        Delete
    }
}
