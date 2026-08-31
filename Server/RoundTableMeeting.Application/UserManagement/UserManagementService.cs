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
    IPasswordHasher<UserAccount> passwordHasher)
    : IUserManagementService, IScopedDependency<IUserManagementService>
{
    public async Task<IReadOnlyList<UserSummaryDto>> ListUsersAsync(CancellationToken cancellationToken = default)
        => (await userAccountRepository.ListAllAsync(cancellationToken)).Select(ToDto).ToArray();

    public async Task<UserManagementResult> CreateUserAsync(CreateUserRequest request, CancellationToken cancellationToken = default)
    {
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
        try
        {
            await unitOfWork.CommitAsync(cancellationToken);
        }
        catch (UsernameConflictException)
        {
            return new(UserManagementStatus.UsernameConflict);
        }

        return new(UserManagementStatus.Success, ToDto(account));
    }

    public async Task<UserManagementResult> UpdateNicknameAsync(Guid userId, UpdateNicknameRequest request, CancellationToken cancellationToken = default)
    {
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

        unitOfWork.RegisterEdit(account);
        await unitOfWork.CommitAsync(cancellationToken);
        return new(UserManagementStatus.Success, ToDto(account));
    }

    public async Task<UserManagementResult> ResetPasswordAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var account = await userAccountRepository.FindByIdAsync(userId, cancellationToken);
        if (account is null) return new(UserManagementStatus.UserNotFound);

        account.ReplacePasswordHash(passwordHasher.HashPassword(account, UserManagementConstants.ResetPassword));
        unitOfWork.RegisterEdit(account);
        await unitOfWork.CommitAsync(cancellationToken);
        return new(UserManagementStatus.Success);
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

    private static UserSummaryDto ToDto(UserAccount account) => new(account.ID, account.Username, account.Nickname);
}
