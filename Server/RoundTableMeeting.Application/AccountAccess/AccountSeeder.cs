using Materal.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Identity;
using RoundTableMeeting.Abstraction.Contracts.Authentication;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Domain.UnitOfWork;

namespace RoundTableMeeting.Application.AccountAccess;

public sealed class AccountSeeder(
    IUserAccountRepository userAccountRepository,
    IUnitOfWork unitOfWork,
    IPasswordHasher<UserAccount> passwordHasher)
    : IAccountSeeder, IScopedDependency<IAccountSeeder>
{
    public async Task SeedAsync(IReadOnlyCollection<SeedAccountInput> accounts, CancellationToken cancellationToken = default)
    {
        if (accounts.Count == 0) throw new InvalidOperationException("At least one authentication seed account is required.");

        var normalized = accounts.Select(account =>
        {
            var username = UserAccount.NormalizeDisplayUsername(account.Username);
            var nickname = UserAccount.NormalizeNickname(account.Nickname);
            if (string.IsNullOrEmpty(account.Password) || account.Password.Length > 256)
            {
                throw new InvalidOperationException("Authentication seed passwords are invalid.");
            }

            return (username, normalizedUsername: UserAccount.NormalizeUsername(username), nickname, account.Password);
        }).ToArray();

        if (normalized.Select(account => account.normalizedUsername).Distinct(StringComparer.Ordinal).Count() != normalized.Length)
        {
            throw new InvalidOperationException("Authentication seed usernames must be unique.");
        }

        var changed = false;
        foreach (var seed in normalized)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = await userAccountRepository.FindByNormalizedUsernameAsync(seed.normalizedUsername, cancellationToken);
            if (existing is not null) continue;

            var account = UserAccount.Create(seed.username, seed.nickname);
            account.InitializePasswordHash(passwordHasher.HashPassword(account, seed.Password));
            unitOfWork.RegisterAdd(account);
            changed = true;
        }

        if (changed) await unitOfWork.CommitAsync(cancellationToken);
    }
}
