using Materal.Extensions.DependencyInjection;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Repositories;

namespace RoundTableMeeting.Application.AccountAccess;

public sealed class AdministratorAccessService(IUserAccountRepository repository, ICurrentUserContext currentUser)
    : IAdministratorAccessService, IScopedDependency<IAdministratorAccessService>
{
    public async Task<bool> IsAdministratorAsync(CancellationToken cancellationToken = default)
        => currentUser.IsAuthenticated && currentUser.UserId is { } id
            && await repository.FindByIdAsync(id, cancellationToken) is { IsAdmin: true };
}
