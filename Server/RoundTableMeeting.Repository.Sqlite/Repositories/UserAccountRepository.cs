using Microsoft.EntityFrameworkCore;
using Materal.Extensions.DependencyInjection;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.Repository.Sqlite.Repositories;

public sealed class UserAccountRepository : SqliteRepository<UserAccount, Guid>, IUserAccountRepository, IScopedDependency<IUserAccountRepository>
{
    private readonly RoundTableMeetingDbContext dbContext;

    public UserAccountRepository(RoundTableMeetingDbContext dbContext)
        : base(dbContext)
    {
        this.dbContext = dbContext;
    }

    public Task<UserAccount?> FindByNormalizedUsernameAsync(string normalizedUsername, CancellationToken cancellationToken = default)
        => dbContext.UserAccounts.SingleOrDefaultAsync(account => account.NormalizedUsername == normalizedUsername, cancellationToken);

    public Task<UserAccount?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => dbContext.UserAccounts.SingleOrDefaultAsync(account => account.ID == id, cancellationToken);
}
