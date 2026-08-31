using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Repository.Sqlite.Database;
using RoundTableMeeting.Repository.Sqlite.Repositories;
using RoundTableMeeting.Repository.Sqlite.UnitOfWork;

namespace RoundTableMeeting.Repository.Sqlite.Tests;

public sealed class UserAccountRepositoryTests
{
    [Fact]
    public async Task UserAccountsMapAndQueryWithoutTracking()
    {
        var root = Path.Combine(Path.GetTempPath(), "RoundTableMeetingUserTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "accounts.db");
        try
        {
            Directory.CreateDirectory(root);
            await using var context = CreateContext(path);
            await context.Database.MigrateAsync();
            var account = UserAccount.Create(" Alice ", "Alice");
            account.InitializePasswordHash("hash");
            context.UserAccounts.Add(account);
            await context.SaveChangesAsync();

            var repository = new UserAccountRepository(context);
            var byName = await repository.FindByNormalizedUsernameAsync("ALICE");
            var byId = await repository.FindByIdAsync(account.ID);

            Assert.Equal(account.ID, byName?.ID);
            Assert.Equal(account.ID, byId?.ID);
            Assert.Equal(EntityState.Detached, context.Entry(byName!).State);
            Assert.Equal(EntityState.Detached, context.Entry(byId!).State);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task NormalizedUsernameUniqueIndexRejectsDuplicates()
    {
        var root = Path.Combine(Path.GetTempPath(), "RoundTableMeetingUserTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "accounts.db");
        try
        {
            Directory.CreateDirectory(root);
            await using var context = CreateContext(path);
            await context.Database.MigrateAsync();
            var first = UserAccount.Create("alice", "Alice");
            first.InitializePasswordHash("hash");
            var second = UserAccount.Create("ALICE", "Other");
            second.InitializePasswordHash("hash");
            context.UserAccounts.AddRange(first, second);

            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task UnicodeEquivalentUsernamesAreRejectedByTheUniqueIndex()
    {
        var root = Path.Combine(Path.GetTempPath(), "RoundTableMeetingUserTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "accounts.db");
        try
        {
            Directory.CreateDirectory(root);
            await using var context = CreateContext(path);
            await context.Database.MigrateAsync();
            var first = UserAccount.Create("café", "Cafe");
            first.InitializePasswordHash("hash");
            var second = UserAccount.Create("café", "Other");
            second.InitializePasswordHash("hash");
            context.UserAccounts.AddRange(first, second);

            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task MigrationContainsFrozenColumnsAndUniqueIndex()
    {
        var root = Path.Combine(Path.GetTempPath(), "RoundTableMeetingUserTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "accounts.db");
        try
        {
            Directory.CreateDirectory(root);
            await using var context = CreateContext(path);
            await context.Database.MigrateAsync();
            await using var connection = new SqliteConnection("Data Source=" + path);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA table_info(UserAccounts);";
            var columns = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
            }
            Assert.Equal(["ID", "Username", "NormalizedUsername", "Nickname", "PasswordHash"], columns);
            command.CommandText = "PRAGMA index_list(UserAccounts);";
            var indexText = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync()) indexText.Add(reader.GetString(1));
            }
            Assert.Contains("IX_UserAccounts_NormalizedUsername", indexText);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task EmptyAccountCannotBePersisted()
    {
        var root = Path.Combine(Path.GetTempPath(), "RoundTableMeetingUserTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "accounts.db");
        try
        {
            Directory.CreateDirectory(root);
            await using var context = CreateContext(path);
            await context.Database.MigrateAsync();
            context.UserAccounts.Add(new UserAccount());

            await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task NicknameColumnUsesTheFrozen64CharacterLimit()
    {
        var root = Path.Combine(Path.GetTempPath(), "RoundTableMeetingUserTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "accounts.db");
        try
        {
            Directory.CreateDirectory(root);
            await using var context = CreateContext(path);
            await context.Database.MigrateAsync();
            var property = context.Model.FindEntityType(typeof(UserAccount))!.FindProperty(nameof(UserAccount.Nickname))!;

            Assert.Equal(64, property.GetMaxLength());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ProductionRepositoryAndUnitOfWorkShareTheSameScopedDbContext()
    {
        var root = Path.Combine(Path.GetTempPath(), "RoundTableMeetingUserTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "accounts.db");
        try
        {
            Directory.CreateDirectory(root);
            await using var context = CreateContext(path);
            await context.Database.MigrateAsync();
            var repository = new UserAccountRepository(context);
            var unitOfWork = new UnitOfWorkImpl(context);
            var account = UserAccount.Create("scoped", "Scoped");
            account.InitializePasswordHash("hash");
            unitOfWork.RegisterAdd(account);
            Assert.Equal(1, await unitOfWork.CommitAsync());
            Assert.Equal(account.ID, (await repository.FindByNormalizedUsernameAsync("SCOPED"))?.ID);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ListAllUsesStableNormalizedUsernameAndIdOrdering()
    {
        var root = Path.Combine(Path.GetTempPath(), "RoundTableMeetingUserTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "accounts.db");
        try
        {
            Directory.CreateDirectory(root);
            await using var context = CreateContext(path);
            await context.Database.MigrateAsync();
            foreach (var username in new[] { "charlie", "alice", "bob" })
            {
                var account = UserAccount.Create(username, username);
                account.InitializePasswordHash("hash");
                context.UserAccounts.Add(account);
            }
            await context.SaveChangesAsync();

            var result = await new UserAccountRepository(context).ListAllAsync();

            Assert.Equal(["alice", "bob", "charlie"], result.Select(account => account.Username));
            Assert.All(result, account => Assert.Equal(EntityState.Detached, context.Entry(account).State));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task UnitOfWorkTranslatesOnlyUsernameUniqueIndexConflict()
    {
        var root = Path.Combine(Path.GetTempPath(), "RoundTableMeetingUserTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "accounts.db");
        try
        {
            Directory.CreateDirectory(root);
            await using var context = CreateContext(path);
            await context.Database.MigrateAsync();
            var first = UserAccount.Create("alice", "Alice");
            first.InitializePasswordHash("hash");
            context.UserAccounts.Add(first);
            await context.SaveChangesAsync();
            var duplicate = UserAccount.Create("ALICE", "Other");
            duplicate.InitializePasswordHash("hash");
            var unitOfWork = new UnitOfWorkImpl(context);
            unitOfWork.RegisterAdd(duplicate);

            await Assert.ThrowsAsync<RoundTableMeeting.Domain.Exceptions.UsernameConflictException>(() => unitOfWork.CommitAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static RoundTableMeetingDbContext CreateContext(string path)
        => new(new DbContextOptionsBuilder<RoundTableMeetingDbContext>()
            .UseSqlite("Data Source=" + path)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options);
}
