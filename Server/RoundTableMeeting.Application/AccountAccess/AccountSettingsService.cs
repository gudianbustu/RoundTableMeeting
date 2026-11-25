using Materal.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Identity;
using RoundTableMeeting.Abstraction.Contracts.Authentication;
using RoundTableMeeting.Abstraction.Contracts.UserManagement;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Repositories;

namespace RoundTableMeeting.Application.AccountAccess;

public sealed class AccountSettingsService(IUserAccountRepository repository, ICurrentUserContext currentUser,
    IAccountTransactionRunner transactionRunner, IPasswordHasher<UserAccount> passwordHasher,
    PasswordVerificationService passwordVerification, IAvatarRepository? avatars = null)
    : IAccountSettingsService, IScopedDependency<IAccountSettingsService>
{
    public Task<AccountSettingsResult> UpdateMyNicknameAsync(UpdateNicknameRequest request, CancellationToken cancellationToken = default)
        => transactionRunner.RunAsync(async token =>
        {
            var account = await FindCurrentAsync(token);
            if (account is null) return new AccountSettingsResult(AccountSettingsStatus.Unauthenticated);
            try { account.ChangeNickname(request.Nickname!); }
            catch (ArgumentException) { return Invalid("nickname"); }
            await repository.UpdateNicknameAsync(account.ID, account.Nickname, token);
            return new AccountSettingsResult(AccountSettingsStatus.Success,
                new(account.ID, account.Username, account.Nickname, account.IsAdmin)
                { AvatarUrl = avatars is null ? null : await avatars.GetAccountAsync(account.ID, token) });
        }, cancellationToken);

    public Task<AccountSettingsResult> ChangeMyPasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken = default)
        => transactionRunner.RunAsync(async token =>
        {
            var account = await FindCurrentAsync(token);
            if (account is null) return new AccountSettingsResult(AccountSettingsStatus.Unauthenticated);
            if (string.IsNullOrEmpty(request.CurrentPassword) || request.CurrentPassword.Length > 256) return Invalid("currentPassword");
            if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length > 256) return Invalid("newPassword");
            if (passwordVerification.Verify(account, request.CurrentPassword) != AccountPasswordVerificationResult.Success)
                return Invalid("currentPassword");
            await repository.UpdatePasswordHashAsync(account.ID, passwordHasher.HashPassword(account, request.NewPassword), token);
            return new AccountSettingsResult(AccountSettingsStatus.Success);
        }, cancellationToken);

    private Task<UserAccount?> FindCurrentAsync(CancellationToken token)
        => currentUser.IsAuthenticated && currentUser.UserId is { } id
            ? repository.FindByIdAsync(id, token) : Task.FromResult<UserAccount?>(null);

    private static AccountSettingsResult Invalid(string field)
        => new(AccountSettingsStatus.ValidationFailed, Errors: new Dictionary<string, string[]> { [field] = ["The value is invalid."] });
}
