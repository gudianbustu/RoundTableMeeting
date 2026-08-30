using RoundTableMeeting.Domain.Entities;
using Materal.TTA.Common;

namespace RoundTableMeeting.Domain.Repositories;

public interface IUserAccountRepository : IRepository<UserAccount, Guid>
{
    Task<UserAccount?> FindByNormalizedUsernameAsync(string normalizedUsername, CancellationToken cancellationToken = default);

    Task<UserAccount?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);
}
