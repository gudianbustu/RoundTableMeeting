using Materal.Extensions.DependencyInjection;
using System.Text;
using RoundTableMeeting.Abstraction.Contracts.Authentication;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Repositories;

namespace RoundTableMeeting.Application.AccountAccess;

public sealed class AuthenticationService(
    IUserAccountRepository userAccountRepository,
    ICurrentUserContext currentUserContext,
    PasswordVerificationService passwordVerificationService)
    : IAuthenticationService, IScopedDependency<IAuthenticationService>
{
    public async Task<AuthenticateResult> AuthenticateAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var errors = Validate(request);
        if (errors.Count > 0)
        {
            return new AuthenticateResult(AuthenticateStatus.ValidationFailed, Errors: errors);
        }

        string normalizedUsername;
        try
        {
            normalizedUsername = UserAccount.NormalizeUsername(request.Username!);
        }
        catch (ArgumentException)
        {
            return new AuthenticateResult(AuthenticateStatus.ValidationFailed, Errors: new Dictionary<string, string[]> { ["username"] = ["Username is invalid."] });
        }
        var account = await userAccountRepository.FindByNormalizedUsernameAsync(normalizedUsername, cancellationToken);
        var verification = passwordVerificationService.Verify(account, request.Password!);
        if (verification != AccountPasswordVerificationResult.Success || account is null)
        {
            return new AuthenticateResult(AuthenticateStatus.InvalidCredentials);
        }

        return new AuthenticateResult(AuthenticateStatus.Success, ToDto(account));
    }

    public async Task<CurrentUserResult> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        if (!currentUserContext.IsAuthenticated || currentUserContext.UserId is not { } userId)
        {
            return new CurrentUserResult(CurrentUserStatus.Unauthenticated);
        }

        var account = await userAccountRepository.FindByIdAsync(userId, cancellationToken);
        return account is null
            ? new CurrentUserResult(CurrentUserStatus.Unauthenticated)
            : new CurrentUserResult(CurrentUserStatus.Success, ToDto(account));
    }

    private static Dictionary<string, string[]> Validate(LoginRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(request.Username))
        {
            errors["username"] = ["Username is required and must be at most 64 characters after normalization."];
        }
        else
        {
            try { _ = UserAccount.NormalizeUsername(request.Username); }
            catch (ArgumentException) { errors["username"] = ["Username is required and must be at most 64 characters after normalization."]; }
        }

        if (string.IsNullOrEmpty(request.Password) || request.Password.Length > 256)
        {
            errors["password"] = ["Password is required and must be at most 256 characters."];
        }

        return errors;
    }

    private static CurrentUserDto ToDto(UserAccount account) => new(account.ID, account.Username, account.Nickname);
}
