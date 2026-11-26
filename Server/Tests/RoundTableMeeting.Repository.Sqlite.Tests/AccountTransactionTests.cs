using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Repository.Sqlite.Database;
using RoundTableMeeting.Repository.Sqlite.Repositories;
using RoundTableMeeting.Repository.Sqlite.Transactions;
using RoundTableMeeting.Repository.Sqlite.UnitOfWork;

namespace RoundTableMeeting.Repository.Sqlite.Tests;

public sealed class AccountTransactionTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"account-{Guid.NewGuid():N}.db");
    private RoundTableMeetingDbContext Context() => new(new DbContextOptionsBuilder<RoundTableMeetingDbContext>()
        .UseSqlite($"Data Source={path};Pooling=False").UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking).Options);
    public void Dispose() { if (File.Exists(path)) File.Delete(path); }

    [Fact]
    public async Task UpgradePreservesExistingAccountAndDefaultsToOrdinary()
    {
        await using var context = Context();
        var migrations = context.Database.GetMigrations().ToArray();
        var adminMigration = Array.FindIndex(migrations, item => item.EndsWith("AddUserAccountAdmin", StringComparison.Ordinal));
        Assert.True(adminMigration > 0);
        await context.GetService<IMigrator>().MigrateAsync(migrations[adminMigration - 1], TestContext.Current.CancellationToken);
        var id = Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO UserAccounts (ID, Username, NormalizedUsername, Nickname, PasswordHash) VALUES ({id}, {"old"}, {"OLD"}, {"Original"}, {"original-hash"})", cancellationToken: TestContext.Current.CancellationToken);
        await context.Database.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var account = await context.UserAccounts.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(id, account.ID); Assert.False(account.IsAdmin);
        Assert.Equal("Original", account.Nickname); Assert.Equal("original-hash", account.PasswordHash);
        await context.Database.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(migrations.Length, (await context.Database.GetAppliedMigrationsAsync(cancellationToken: TestContext.Current.CancellationToken)).Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExceptionsAndCancellationRollBackAndRunnerCanBeReused(bool cancel)
    {
        await using var context = Context(); await context.Database.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var uow = new UnitOfWorkImpl(context); var runner = new AccountTransactionRunner(context, uow);
        var account = UserAccount.Create("test", "Original"); account.InitializePasswordHash("hash");
        await runner.RunAsync(token => { uow.RegisterAdd(account); return Task.FromResult(true); }, TestContext.Current.CancellationToken);
        var repository = new UserAccountRepository(context);
        using var source = new CancellationTokenSource();
        async Task Execute() => await runner.RunAsync(async token => {
            await repository.UpdateNicknameAsync(account.ID, "Changed", token);
            if (cancel) { source.Cancel(); token.ThrowIfCancellationRequested(); }
            throw new InvalidOperationException("test rollback");
#pragma warning disable CS0162
            return true;
#pragma warning restore CS0162
        }, source.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Execute);
        else await Assert.ThrowsAsync<InvalidOperationException>(Execute);
        Assert.Null(context.Database.CurrentTransaction); Assert.Empty(context.ChangeTracker.Entries());
        await using var verify = Context(); Assert.Equal("Original", (await verify.UserAccounts.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).Nickname);
        await runner.RunAsync(async token => { await repository.UpdateAdministratorAsync(account.ID, true, token); return true; }, TestContext.Current.CancellationToken);
        Assert.True((await verify.UserAccounts.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).IsAdmin);
    }
}
