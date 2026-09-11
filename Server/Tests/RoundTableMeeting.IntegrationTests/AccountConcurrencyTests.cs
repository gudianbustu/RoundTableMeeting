using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using RoundTableMeeting.Abstraction.Contracts.Authentication;
using RoundTableMeeting.Abstraction.Contracts.UserManagement;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Application.AccountAccess;
using RoundTableMeeting.Application.UserManagement;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Domain.UnitOfWork;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed class AccountConcurrencyTests
{
    private sealed record Actor(Guid? UserId) : ICurrentUserContext { public bool IsAuthenticated => UserId is not null; }
    private static UserManagementService Management(IServiceProvider services, Guid id) => new(
        services.GetRequiredService<IUserAccountRepository>(), services.GetRequiredService<IUnitOfWork>(),
        services.GetRequiredService<IPasswordHasher<UserAccount>>(), new Actor(id), services.GetRequiredService<IAccountTransactionRunner>());
    private static AccountSettingsService Settings(IServiceProvider services, Guid id) => new(
        services.GetRequiredService<IUserAccountRepository>(), new Actor(id), services.GetRequiredService<IAccountTransactionRunner>(),
        services.GetRequiredService<IPasswordHasher<UserAccount>>(), services.GetRequiredService<PasswordVerificationService>());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentDemotionsNeverRemoveAllAdministrators(bool self)
    {
        using var factory = new TestApplicationFactory(); using var init = factory.Services.CreateScope();
        var repo = init.ServiceProvider.GetRequiredService<IUserAccountRepository>();
        var a = (await repo.FindByNormalizedUsernameAsync("ALICE"))!; var b = (await repo.FindByNormalizedUsernameAsync("BOB"))!;
        await init.ServiceProvider.GetRequiredService<IAccountTransactionRunner>().RunAsync(async token => { await repo.UpdateAdministratorAsync(b.ID, true, token); return true; });
        using var barrier = new Barrier(2);
        Task<UserManagementResult> Run(Guid actor, Guid target) => Task.Run(async () => {
            using var scope = factory.Services.CreateScope(); barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            return await Management(scope.ServiceProvider, actor).UpdateAdminAsync(target, new() { IsAdmin = false });
        });
        var results = await Task.WhenAll(Run(a.ID, self ? a.ID : b.ID), Run(b.ID, self ? b.ID : a.ID));
        Assert.Single(results, result => result.Status == UserManagementStatus.Success);
        Assert.Contains(results, result => result.Status is UserManagementStatus.Forbidden or UserManagementStatus.LastAdminRequired);
        Assert.Equal(1, await repo.CountAdministratorsAsync());
    }

    [Fact]
    public async Task CompetingPasswordChangesVerifyLatestCommittedPassword()
    {
        using var factory = new TestApplicationFactory(); using var init = factory.Services.CreateScope();
        var repo = init.ServiceProvider.GetRequiredService<IUserAccountRepository>(); var b = (await repo.FindByNormalizedUsernameAsync("BOB"))!;
        using var barrier = new Barrier(2);
        Task<AccountSettingsResult> Run(string password) => Task.Run(async () => {
            using var scope = factory.Services.CreateScope(); barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            return await Settings(scope.ServiceProvider, b.ID).ChangeMyPasswordAsync(new() { CurrentPassword = "test-password-bob", NewPassword = password });
        });
        var results = await Task.WhenAll(Run("first"), Run("second"));
        Assert.Single(results, x => x.Status == AccountSettingsStatus.Success);
        Assert.Single(results, x => x.Status == AccountSettingsStatus.ValidationFailed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResetAndPersonalChangeFollowCommitOrder(bool resetFirst)
    {
        using var factory = new TestApplicationFactory(); using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider; var repo = services.GetRequiredService<IUserAccountRepository>();
        var a = (await repo.FindByNormalizedUsernameAsync("ALICE"))!; var b = (await repo.FindByNormalizedUsernameAsync("BOB"))!;
        var management = Management(services, a.ID); var settings = Settings(services, b.ID);
        if (resetFirst) await management.ResetPasswordAsync(b.ID);
        var change = await settings.ChangeMyPasswordAsync(new() { CurrentPassword = "test-password-bob", NewPassword = "personal" });
        Assert.Equal(resetFirst ? AccountSettingsStatus.ValidationFailed : AccountSettingsStatus.Success, change.Status);
        if (!resetFirst) await management.ResetPasswordAsync(b.ID);
        b = (await repo.FindByIdAsync(b.ID))!;
        Assert.NotEqual(PasswordVerificationResult.Failed, services.GetRequiredService<IPasswordHasher<UserAccount>>().VerifyHashedPassword(b, b.PasswordHash, "123456"));
    }

    [Fact]
    public async Task StaleNicknameSnapshotCannotRestoreRoleOrPassword()
    {
        using var factory = new TestApplicationFactory(); using var first = factory.Services.CreateScope(); using var second = factory.Services.CreateScope();
        var r1 = first.ServiceProvider.GetRequiredService<IUserAccountRepository>(); var r2 = second.ServiceProvider.GetRequiredService<IUserAccountRepository>();
        var stale = (await r1.FindByNormalizedUsernameAsync("BOB"))!;
        await second.ServiceProvider.GetRequiredService<IAccountTransactionRunner>().RunAsync(async token => {
            await r2.UpdateAdministratorAsync(stale.ID, true, token); await r2.UpdatePasswordHashAsync(stale.ID, "changed-hash", token); return true;
        });
        await Settings(first.ServiceProvider, stale.ID).UpdateMyNicknameAsync(new() { Nickname = "Updated" });
        var actual = (await r1.FindByIdAsync(stale.ID))!;
        Assert.True(actual.IsAdmin); Assert.Equal("changed-hash", actual.PasswordHash); Assert.Equal("Updated", actual.Nickname);
    }

    [Fact]
    public async Task InitializationOfExistingAccountsCommitsPromotionAndDoesNotRestoreRevokedRole()
    {
        using var factory = new TestApplicationFactory(); using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider; var repo = services.GetRequiredService<IUserAccountRepository>();
        var a = (await repo.FindByNormalizedUsernameAsync("ALICE"))!; var originalHash = a.PasswordHash;
        await services.GetRequiredService<IAccountTransactionRunner>().RunAsync(async token => { await repo.UpdateAdministratorAsync(a.ID, false, token); return true; });
        using var barrier = new Barrier(2);
        Task Run() => Task.Run(async () => {
            using var child = factory.Services.CreateScope(); barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            await child.ServiceProvider.GetRequiredService<IAccountSeeder>().SeedAsync([new("alice", "Changed", "changed")], initialAdminUsername: "ALICE");
        });
        await Task.WhenAll(Run(), Run());
        a = (await repo.FindByIdAsync(a.ID))!;
        Assert.True(a.IsAdmin); Assert.Equal(originalHash, a.PasswordHash); Assert.Equal("Alice", a.Nickname);
        var b = (await repo.FindByNormalizedUsernameAsync("BOB"))!;
        await Management(services, a.ID).UpdateAdminAsync(b.ID, new() { IsAdmin = true });
        await Management(services, b.ID).UpdateAdminAsync(a.ID, new() { IsAdmin = false });
        await services.GetRequiredService<IAccountSeeder>().SeedAsync([new("alice", "Changed", "changed")], initialAdminUsername: "alice");
        Assert.False((await repo.FindByIdAsync(a.ID))!.IsAdmin);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("missing")]
    public async Task FailedInitializationRollsBackNewAccounts(string? initialAdmin)
    {
        using var factory = new TestApplicationFactory(); using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider; var repo = services.GetRequiredService<IUserAccountRepository>();
        var a = (await repo.FindByNormalizedUsernameAsync("ALICE"))!;
        await services.GetRequiredService<IAccountTransactionRunner>().RunAsync(async token => { await repo.UpdateAdministratorAsync(a.ID, false, token); return true; });
        await Assert.ThrowsAsync<InvalidOperationException>(() => services.GetRequiredService<IAccountSeeder>().SeedAsync([new("new", "New", "secret")], initialAdminUsername: initialAdmin));
        Assert.Null(await repo.FindByNormalizedUsernameAsync("NEW"));
        Assert.Equal(0, await repo.CountAdministratorsAsync());
    }
}
