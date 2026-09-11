using Materal.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Identity;
using RoundTableMeeting.Abstraction.Contracts.Authentication;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Domain.UnitOfWork;

namespace RoundTableMeeting.Application.AccountAccess;

/// <summary>
/// 在应用启动时幂等创建配置的初始账号
/// </summary>
public sealed class AccountSeeder(
    IUserAccountRepository userAccountRepository,
    IUnitOfWork unitOfWork,
    IPasswordHasher<UserAccount> passwordHasher,
    IAccountTransactionRunner transactionRunner)
    : IAccountSeeder, IScopedDependency<IAccountSeeder>
{
    /// <summary>
    /// 校验种子账号，创建缺失账号并提交新增记录
    /// </summary>
    public async Task SeedAsync(IReadOnlyCollection<SeedAccountInput> accounts, CancellationToken cancellationToken = default, string? initialAdminUsername = null)
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

        await transactionRunner.RunAsync(async token =>
        {
            var added = new List<UserAccount>();
            foreach (var seed in normalized)
            {
                token.ThrowIfCancellationRequested();
                if (await userAccountRepository.FindByNormalizedUsernameAsync(seed.normalizedUsername, token) is not null) continue;
                var account = UserAccount.Create(seed.username, seed.nickname);
                account.InitializePasswordHash(passwordHasher.HashPassword(account, seed.Password));
                unitOfWork.RegisterAdd(account);
                added.Add(account);
            }

            if (await userAccountRepository.CountAdministratorsAsync(token) == 0)
            {
                string adminName;
                try { adminName = UserAccount.NormalizeUsername(initialAdminUsername ?? string.Empty); }
                catch (ArgumentException) { throw new InvalidOperationException("Authentication:InitialAdminUsername is required when no administrator exists."); }
                var newAdmin = added.SingleOrDefault(account => account.NormalizedUsername == adminName);
                if (newAdmin is not null) newAdmin.SetAdministrator(true);
                else
                {
                    var existing = await userAccountRepository.FindByNormalizedUsernameAsync(adminName, token)
                        ?? throw new InvalidOperationException("The configured initial administrator does not exist.");
                    await userAccountRepository.UpdateAdministratorAsync(existing.ID, true, token);
                }
            }
            return true;
        }, cancellationToken);
    }
}
