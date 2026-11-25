using Materal.Extensions.DependencyInjection;
using System.Text;
using RoundTableMeeting.Abstraction.Contracts.Authentication;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Repositories;

namespace RoundTableMeeting.Application.AccountAccess;

/// <summary>
/// 执行登录凭据校验和当前用户解析
/// </summary>
public sealed class AuthenticationService(
    IUserAccountRepository userAccountRepository,
    ICurrentUserContext currentUserContext,
    PasswordVerificationService passwordVerificationService, IAvatarRepository? avatars = null)
    : IAuthenticationService, IScopedDependency<IAuthenticationService>
{
    /// <summary>
    /// 认证登录请求并返回客户端可用的用户信息
    /// </summary>
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
        // 为未知账号校验伪哈希，避免通过耗时泄露用户名是否存在
        var verification = passwordVerificationService.Verify(account, request.Password!);
        if (verification != AccountPasswordVerificationResult.Success || account is null)
        {
            return new AuthenticateResult(AuthenticateStatus.InvalidCredentials);
        }

        return new AuthenticateResult(AuthenticateStatus.Success, await ToDtoAsync(account, cancellationToken));
    }

    /// <summary>
    /// 根据当前请求声明解析账号信息
    /// </summary>
    public async Task<CurrentUserResult> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        if (!currentUserContext.IsAuthenticated || currentUserContext.UserId is not { } userId)
        {
            return new CurrentUserResult(CurrentUserStatus.Unauthenticated);
        }

        var account = await userAccountRepository.FindByIdAsync(userId, cancellationToken);
        return account is null
            ? new CurrentUserResult(CurrentUserStatus.Unauthenticated)
            : new CurrentUserResult(CurrentUserStatus.Success, await ToDtoAsync(account, cancellationToken));
    }

    /// <summary>
    /// 执行领域级登录校验并生成字段错误
    /// </summary>
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

    /// <summary>
    /// 将领域账号映射为客户端用户资料
    /// </summary>
    private async Task<CurrentUserDto> ToDtoAsync(UserAccount account, CancellationToken ct) =>
        new(account.ID, account.Username, account.Nickname, account.IsAdmin)
        { AvatarUrl = avatars is null ? null : await avatars.GetAccountAsync(account.ID, ct) };
}
