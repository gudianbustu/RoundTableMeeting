using Materal.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Identity;
using RoundTableMeeting.Abstraction.Contracts.UserManagement;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Exceptions;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Domain.UnitOfWork;

namespace RoundTableMeeting.Application.UserManagement;

/// <summary>
/// 执行内部测试阶段的最小用户管理用例。
/// </summary>
public sealed class UserManagementService(
    IUserAccountRepository userAccountRepository,
    IUnitOfWork unitOfWork,
    IPasswordHasher<UserAccount> passwordHasher,
    ICurrentUserContext currentUser,
    IAccountTransactionRunner transactionRunner)
    : IUserManagementService, IScopedDependency<IUserManagementService>
{
    public async Task<IReadOnlyList<UserSummaryDto>> ListUsersAsync(CancellationToken cancellationToken = default)
        => (await userAccountRepository.ListAllAsync(cancellationToken)).Select(ToDto).ToArray();

    public Task<UserManagementResult> RegisterAsync(string username, string password, CancellationToken cancellationToken = default)
        => CreateAccountAsync(new CreateUserRequest { Username = username, Nickname = username, InitialPassword = password }, false, cancellationToken);

    public Task<UserManagementResult> CreateUserAsync(CreateUserRequest request, CancellationToken cancellationToken = default)
        => CreateAccountAsync(request, true, cancellationToken);

    private async Task<UserManagementResult> CreateAccountAsync(CreateUserRequest request, bool requiresAdministrator, CancellationToken cancellationToken)
    {
        try
        {
            return await transactionRunner.RunAsync(async token =>
            {
                if (requiresAdministrator)
                {
                    var access = await CheckAccessAsync(token);
                    if (access is not null) return access;
                }
                var errors = ValidateCreate(request);
                if (errors.Count > 0) return new(UserManagementStatus.ValidationFailed, Errors: errors);

                UserAccount account;
                try
                {
                    account = UserAccount.Create(request.Username!, request.Nickname!);
                }
                catch (ArgumentException exception)
                {
                    return new(UserManagementStatus.ValidationFailed, Errors: new Dictionary<string, string[]>
                    { [FieldFor(exception.ParamName)] = ["The value is invalid."] });
                }

                var normalizedUsername = account.NormalizedUsername;
                if (await userAccountRepository.FindByNormalizedUsernameAsync(normalizedUsername, cancellationToken) is not null)
                {
                    return new(UserManagementStatus.UsernameConflict);
                }

                account.InitializePasswordHash(passwordHasher.HashPassword(account, request.InitialPassword!));
                unitOfWork.RegisterAdd(account);
                return new UserManagementResult(UserManagementStatus.Success, ToDto(account));
            }, cancellationToken);
        }
        catch (UsernameConflictException) { return new(UserManagementStatus.UsernameConflict); }
    }

    public async Task<UserManagementResult> UpdateNicknameAsync(Guid userId, UpdateNicknameRequest request, CancellationToken cancellationToken = default)
    {
        return await transactionRunner.RunAsync(async token =>
        {
            var access = await CheckAccessAsync(token);
            if (access is not null) return access;
            var errors = ValidateNickname(request);
            if (errors.Count > 0) return new(UserManagementStatus.ValidationFailed, Errors: errors);

            var account = await userAccountRepository.FindByIdAsync(userId, cancellationToken);
            if (account is null) return new(UserManagementStatus.UserNotFound);

            try
            {
                account.ChangeNickname(request.Nickname!);
            }
            catch (ArgumentException)
            {
                return new(UserManagementStatus.ValidationFailed, Errors: new Dictionary<string, string[]>
                { ["nickname"] = ["The nickname is invalid."] });
            }

            await userAccountRepository.UpdateNicknameAsync(account.ID, account.Nickname, token);
            return new UserManagementResult(UserManagementStatus.Success, ToDto(account));
        }, cancellationToken);
    }

    public async Task<UserManagementResult> ResetPasswordAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return await transactionRunner.RunAsync(async token =>
        {
            var access = await CheckAccessAsync(token);
            if (access is not null) return access;
            var account = await userAccountRepository.FindByIdAsync(userId, token);
            if (account is null) return new(UserManagementStatus.UserNotFound);

            account.ReplacePasswordHash(passwordHasher.HashPassword(account, UserManagementConstants.ResetPassword));
            await userAccountRepository.UpdatePasswordHashAsync(account.ID, account.PasswordHash, token);
            return new UserManagementResult(UserManagementStatus.Success);
        }, cancellationToken);
    }

    public Task<UserManagementResult> UpdateAdminAsync(Guid userId, UpdateAdminRequest request, CancellationToken cancellationToken = default)
        => transactionRunner.RunAsync(async token =>
        {
            var access = await CheckAccessAsync(token);
            if (access is not null) return access;
            var account = await userAccountRepository.FindByIdAsync(userId, token);
            if (account is null) return new(UserManagementStatus.UserNotFound);
            if (account.IsAdmin && !request.IsAdmin && await userAccountRepository.CountAdministratorsAsync(token) <= 1)
                return new(UserManagementStatus.LastAdminRequired);
            account.SetAdministrator(request.IsAdmin);
            await userAccountRepository.UpdateAdministratorAsync(userId, request.IsAdmin, token);
            return new UserManagementResult(UserManagementStatus.Success, ToDto(account));
        }, cancellationToken);

    private async Task<UserManagementResult?> CheckAccessAsync(CancellationToken token)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } id)
            return new(UserManagementStatus.Unauthenticated);
        var actor = await userAccountRepository.FindByIdAsync(id, token);
        return actor is null ? new(UserManagementStatus.Unauthenticated)
            : actor.IsAdmin ? null : new(UserManagementStatus.Forbidden);
    }

    private static Dictionary<string, string[]> ValidateCreate(CreateUserRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        try { _ = UserAccount.NormalizeUsername(request.Username ?? string.Empty); }
        catch (ArgumentException) { errors["username"] = ["The username is invalid."]; }
        try { _ = UserAccount.NormalizeNickname(request.Nickname ?? string.Empty); }
        catch (ArgumentException) { errors["nickname"] = ["The nickname is invalid."]; }
        if (string.IsNullOrWhiteSpace(request.InitialPassword) || request.InitialPassword.Length > 256)
        {
            errors["initialPassword"] = ["The initial password is invalid."];
        }
        return errors;
    }

    private static Dictionary<string, string[]> ValidateNickname(UpdateNicknameRequest request)
    {
        try { _ = UserAccount.NormalizeNickname(request.Nickname ?? string.Empty); return []; }
        catch (ArgumentException) { return new Dictionary<string, string[]> { ["nickname"] = ["The nickname is invalid."] }; }
    }

    private static string FieldFor(string? parameterName)
        => parameterName?.ToLowerInvariant() switch
        { "nickname" => "nickname", "username" or "value" => "username", _ => "username" };

    private static UserSummaryDto ToDto(UserAccount account) => new(account.ID, account.Username, account.Nickname, account.IsAdmin);
}
