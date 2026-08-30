using RoundTableMeeting.Abstraction.Contracts.Authentication;

namespace RoundTableMeeting.Abstraction.Services;

public interface IAuthenticationService
{
    Task<AuthenticateResult> AuthenticateAsync(LoginRequest request, CancellationToken cancellationToken = default);

    Task<CurrentUserResult> GetCurrentUserAsync(CancellationToken cancellationToken = default);
}
