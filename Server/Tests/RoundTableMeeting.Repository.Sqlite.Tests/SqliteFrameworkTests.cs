using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.UnitOfWork;
using RoundTableMeeting.Repository.Sqlite.Configuration;
using RoundTableMeeting.Repository.Sqlite.Database;
using RoundTableMeeting.Repository.Sqlite.UnitOfWork;
using RoundTableMeeting.Domain.Exceptions;

namespace RoundTableMeeting.Repository.Sqlite.Tests;

public sealed class SqliteFrameworkTests
{
    [Fact]
    public void RelativePathIsNormalizedUnderApplicationBaseDirectory()
    {
        var connectionString = SqlitePathResolver.NormalizeConnectionString("Data Source=data/round-table-meeting.db");
        var source = new SqliteConnectionStringBuilder(connectionString).DataSource;
        Assert.StartsWith(AppContext.BaseDirectory, Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("Data Source=:memory:", false)]
    [InlineData("Data Source=file::memory:?cache=shared", false)]
    [InlineData("Data Source=test.db;Mode=Memory", false)]
    [InlineData("Data Source=test.db;Default Timeout=0", false)]
    [InlineData("Data Source=test.db;Default Timeout=5", true)]
    public void DatabaseOptionsValidationMatchesDeploymentContract(string connectionString, bool expected)
    {
        Assert.Equal(expected, SqliteDatabaseOptions.TryValidate(connectionString, out _));
    }

    [Fact]
    public void InvalidConnectionStringReportsSafeValidationError()
    {
        Assert.False(SqliteDatabaseOptions.TryValidate("Data Source=abc;UnknownOption=1", out var error));
        Assert.NotEmpty(error);
        Assert.DoesNotContain("abc", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializerCreatesMigrationHistoryAndWALDatabase()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RoundTableMeetingSqliteTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "database.db");
        try
        {
            await using var context = CreateContext(path);
            var initializer = new SqliteDatabaseInitializer(
                context,
                Options.Create(new SqliteDatabaseOptions { ConnectionString = "Data Source=" + path }),
                NullLogger<SqliteDatabaseInitializer>.Instance);
            await initializer.InitializeAsync();

            await using (var connection = new SqliteConnection("Data Source=" + path))
            {
                await connection.OpenAsync();
                await using var seedCommand = connection.CreateCommand();
                seedCommand.CommandText = "CREATE TABLE InitializationProbe (Value TEXT NOT NULL); INSERT INTO InitializationProbe VALUES ('preserved');";
                await seedCommand.ExecuteNonQueryAsync();
            }

            await initializer.InitializeAsync();

            Assert.True(File.Exists(path));
            await using var verificationContext = CreateContext(path);
            await verificationContext.Database.OpenConnectionAsync();
            await using var command = verificationContext.Database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA journal_mode;";
            Assert.Equal("wal", Convert.ToString(await command.ExecuteScalarAsync()), ignoreCase: true);
            command.CommandText = "PRAGMA busy_timeout;";
            Assert.Equal(5000L, Convert.ToInt64(await command.ExecuteScalarAsync()));
            command.CommandText = "SELECT Value FROM InitializationProbe;";
            Assert.Equal("preserved", Convert.ToString(await command.ExecuteScalarAsync()));
            command.CommandText = "SELECT COUNT(*) FROM __EFMigrationsHistory;";
            Assert.Equal(2L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task InitializerPropagatesDatabaseOpenFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RoundTableMeetingSqliteTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var context = CreateContext(directory);
            var initializer = new SqliteDatabaseInitializer(
                context,
                Options.Create(new SqliteDatabaseOptions { ConnectionString = "Data Source=" + directory }),
                NullLogger<SqliteDatabaseInitializer>.Instance);

            await Assert.ThrowsAsync<SqliteException>(() => initializer.InitializeAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task InitializerPropagatesMigrationFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RoundTableMeetingSqliteTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "database.db");
        try
        {
            Directory.CreateDirectory(directory);
            await using (var connection = new SqliteConnection("Data Source=" + path))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE __EFMigrationsHistory (MigrationId INTEGER NOT NULL PRIMARY KEY, ProductVersion TEXT NOT NULL);";
                await command.ExecuteNonQueryAsync();
            }

            await using var context = CreateContext(path);
            var initializer = new SqliteDatabaseInitializer(
                context,
                Options.Create(new SqliteDatabaseOptions { ConnectionString = "Data Source=" + path }),
                NullLogger<SqliteDatabaseInitializer>.Instance);

            await Assert.ThrowsAsync<SqliteException>(() => initializer.InitializeAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void DesignTimeFactoryDoesNotOpenOrInitializeDatabase()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RoundTableMeetingSqliteTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "design-time.db");
        var previous = Environment.GetEnvironmentVariable("ROUND_TABLE_MEETING_DESIGN_TIME_CONNECTION");
        try
        {
            Environment.SetEnvironmentVariable("ROUND_TABLE_MEETING_DESIGN_TIME_CONNECTION", "Data Source=" + path);
            using var context = new RoundTableMeetingDbContextFactory().CreateDbContext([]);

            Assert.Equal(QueryTrackingBehavior.NoTracking, context.ChangeTracker.QueryTrackingBehavior);
            Assert.Equal("Microsoft.EntityFrameworkCore.Sqlite", context.Database.ProviderName);
            Assert.Equal(Path.GetFullPath(path), Path.GetFullPath(context.Database.GetDbConnection().DataSource));
            Assert.False(File.Exists(path));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ROUND_TABLE_MEETING_DESIGN_TIME_CONNECTION", previous);
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task InitializerAppliesBusyTimeoutToMultipleIndependentConnections()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RoundTableMeetingSqliteTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "database.db");
        try
        {
            await using (var context = CreateContext(path))
            {
                var initializer = new SqliteDatabaseInitializer(
                    context,
                    Options.Create(new SqliteDatabaseOptions { ConnectionString = "Data Source=" + path }),
                    NullLogger<SqliteDatabaseInitializer>.Instance);
                await initializer.InitializeAsync();
            }

            SqliteConnection.ClearAllPools();
            await using var first = CreateContext(path);
            await using var second = CreateContext(path);
            await first.Database.OpenConnectionAsync();
            await second.Database.OpenConnectionAsync();

            Assert.Equal(5000L, await ReadPragmaAsync(first, "busy_timeout"));
            Assert.Equal(5000L, await ReadPragmaAsync(second, "busy_timeout"));
            Assert.Equal("wal", Convert.ToString(await ReadPragmaAsync(first, "journal_mode")), ignoreCase: true);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task NoTrackingQueryCanBeEditedAndPersistedThroughUnitOfWork()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RoundTableMeetingSqliteTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "database.db");
        try
        {
            Directory.CreateDirectory(directory);
            await using (var seed = CreateTestContext(path))
            {
                await seed.Database.EnsureCreatedAsync();
                seed.Entities.Add(new TestEntity { Name = "before", Description = "preserve" });
                await seed.SaveChangesAsync();
            }

            await using (var query = CreateTestContext(path))
            {
                var entity = await query.Entities.SingleAsync();
                Assert.Equal(EntityState.Detached, query.Entry(entity).State);
                entity.Name = "after";
                var unitOfWork = new EfUnitOfWorkCore<TestDbContext>(query);
                unitOfWork.RegisterEdit(entity);
                Assert.Equal(EntityState.Modified, query.Entry(entity).State);
                Assert.Equal(1, await unitOfWork.CommitAsync());
            }

            await using var verify = CreateTestContext(path);
            var saved = await verify.Entities.SingleAsync();
            Assert.Equal("after", saved.Name);
            Assert.Equal("preserve", saved.Description);
            Assert.Equal(EntityState.Detached, verify.Entry(saved).State);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task UnitOfWorkTracksDetachedEntityAndClearsAfterCommit()
    {
        await using var context = CreateTestContext(":memory:");
        await context.Database.OpenConnectionAsync();
        await context.Database.EnsureCreatedAsync();
        var unitOfWork = new EfUnitOfWorkCore<TestDbContext>(context);
        var entity = new TestEntity();
        unitOfWork.RegisterAdd(entity);
        Assert.Equal(EntityState.Added, context.Entry(entity).State);
        Assert.Equal(1, await unitOfWork.CommitAsync());
        Assert.Equal(1, context.SaveChangesCalls);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public void NullRegistrationIsRejected()
    {
        using var context = CreateContext(":memory:");
        var unitOfWork = new UnitOfWorkImpl(context);
        Assert.Throws<ArgumentNullException>(() => unitOfWork.RegisterAdd(null!));
    }

    [Fact]
    public void RegisterEditAndDeleteFollowTheFrozenStateTable()
    {
        using var context = CreateTestContext(":memory:");
        var unitOfWork = new EfUnitOfWorkCore<TestDbContext>(context);

        var added = new TestEntity();
        unitOfWork.RegisterAdd(added);
        unitOfWork.RegisterEdit(added);
        Assert.Equal(EntityState.Added, context.Entry(added).State);
        unitOfWork.RegisterDelete(added);
        Assert.Equal(EntityState.Detached, context.Entry(added).State);

        var unchanged = new TestEntity();
        context.Attach(unchanged);
        unitOfWork.RegisterEdit(unchanged);
        Assert.Equal(EntityState.Modified, context.Entry(unchanged).State);
        unitOfWork.RegisterDelete(unchanged);
        Assert.Equal(EntityState.Deleted, context.Entry(unchanged).State);
        unitOfWork.RegisterDelete(unchanged);
        Assert.Equal(EntityState.Deleted, context.Entry(unchanged).State);

        var modified = new TestEntity();
        context.Entry(modified).State = EntityState.Modified;
        Assert.Throws<InvalidOperationException>(() => unitOfWork.RegisterAdd(modified));
        unitOfWork.RegisterEdit(modified);
        Assert.Equal(EntityState.Modified, context.Entry(modified).State);
        unitOfWork.RegisterDelete(modified);
        Assert.Equal(EntityState.Deleted, context.Entry(modified).State);

        var deleted = new TestEntity();
        context.Entry(deleted).State = EntityState.Deleted;
        Assert.Throws<InvalidOperationException>(() => unitOfWork.RegisterEdit(deleted));
        Assert.Throws<InvalidOperationException>(() => unitOfWork.RegisterAdd(deleted));
        unitOfWork.RegisterDelete(deleted);
        Assert.Equal(EntityState.Deleted, context.Entry(deleted).State);

        var unchangedForAdd = new TestEntity();
        context.Attach(unchangedForAdd);
        Assert.Throws<InvalidOperationException>(() => unitOfWork.RegisterAdd(unchangedForAdd));
    }

    [Fact]
    public void InvalidStateRegistrationsAreRejected()
    {
        using var context = CreateTestContext(":memory:");
        var unitOfWork = new EfUnitOfWorkCore<TestDbContext>(context);
        Assert.Throws<ArgumentNullException>(() => unitOfWork.RegisterEdit(null!));
        Assert.Throws<ArgumentNullException>(() => unitOfWork.RegisterDelete(null!));
    }

    [Fact]
    public async Task CommitWithoutChangesReturnsZeroAndClearsTracking()
    {
        await using var context = CreateTestContext(":memory:");
        await context.Database.OpenConnectionAsync();
        await context.Database.EnsureCreatedAsync();
        var unitOfWork = new EfUnitOfWorkCore<TestDbContext>(context);
        var entity = new TestEntity();
        context.Attach(entity);

        Assert.Equal(0, await unitOfWork.CommitAsync());
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task CommitFailureIsRethrownAndTrackerIsCleared()
    {
        await using var context = CreateTestContext(":memory:");
        context.SaveMode = SaveMode.Fail;
        var unitOfWork = new EfUnitOfWorkCore<TestDbContext>(context);
        unitOfWork.RegisterAdd(new TestEntity());

        await Assert.ThrowsAsync<DbUpdateException>(() => unitOfWork.CommitAsync());
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Equal(1, context.SaveChangesCalls);
    }

    [Fact]
    public async Task ConcurrencyFailureIsTranslatedAndTrackerIsCleared()
    {
        await using var context = CreateTestContext(":memory:");
        context.SaveMode = SaveMode.Concurrency;
        var unitOfWork = new EfUnitOfWorkCore<TestDbContext>(context);
        unitOfWork.RegisterAdd(new TestEntity());

        var exception = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => unitOfWork.CommitAsync());
        Assert.IsType<DbUpdateConcurrencyException>(exception.InnerException);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task CancelledCommitIsRethrownAndTrackerIsCleared()
    {
        await using var context = CreateTestContext(":memory:");
        context.SaveMode = SaveMode.Cancel;
        var unitOfWork = new EfUnitOfWorkCore<TestDbContext>(context);
        unitOfWork.RegisterAdd(new TestEntity());

        await Assert.ThrowsAsync<OperationCanceledException>(() => unitOfWork.CommitAsync(new CancellationToken(true)));
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task ConcurrentCommitDoesNotProduceTwoSuccessfulResults()
    {
        await using var context = CreateTestContext(":memory:");
        context.SaveMode = SaveMode.Block;
        var unitOfWork = new EfUnitOfWorkCore<TestDbContext>(context);
        unitOfWork.RegisterAdd(new TestEntity());
        var first = unitOfWork.CommitAsync();
        await context.SaveStarted.Task;
        var second = unitOfWork.CommitAsync();
        context.ReleaseSave.TrySetResult();
        var results = await Task.WhenAll(first.ContinueWith(task => task.Exception?.GetBaseException() ?? (object)task.Result), second.ContinueWith(task => task.Exception?.GetBaseException() ?? (object)task.Result));

        Assert.Contains(results, result => result is Exception);
        Assert.DoesNotContain(results, result => result is int value && value > 0);
    }

    [Fact]
    public async Task AvailabilityCheckerDistinguishesInternalTimeoutFromCallerCancellation()
    {
        await using var context = CreateContext(":memory:");
        var logger = NullLogger<SqliteDatabaseAvailabilityChecker>.Instance;
        var timeoutChecker = new SqliteDatabaseAvailabilityChecker(context, logger, TimeSpan.FromMilliseconds(10), async token => await Task.Delay(Timeout.InfiniteTimeSpan, token));
        Assert.False(await timeoutChecker.IsAvailableAsync());

        using var callerCancellation = new CancellationTokenSource();
        callerCancellation.Cancel();
        var cancelledChecker = new SqliteDatabaseAvailabilityChecker(context, logger, TimeSpan.FromSeconds(1), async token => await Task.Delay(Timeout.InfiniteTimeSpan, token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledChecker.IsAvailableAsync(callerCancellation.Token));
    }

    [Fact]
    public void SameTypeAndIdDifferentInstanceIsRejected()
    {
        using var context = CreateTestContext(":memory:");
        var unitOfWork = new EfUnitOfWorkCore<TestDbContext>(context);
        var first = new TestEntity();
        var second = new TestEntity();
        typeof(BaseEntity).GetProperty(nameof(BaseEntity.ID))!.SetValue(second, first.ID);
        unitOfWork.RegisterAdd(first);

        Assert.Throws<InvalidOperationException>(() => unitOfWork.RegisterAdd(second));
    }

    [Fact]
    public void NonEntityTrackedObjectsDoNotBreakKeyChecks()
    {
        using var context = CreateTestContext(":memory:");
        var unitOfWork = new EfUnitOfWorkCore<TestDbContext>(context);
        var entity = new TestEntity();
        var unrelated = new NonEntity { Key = Guid.NewGuid() };
        context.Entry(unrelated).State = EntityState.Added;

        unitOfWork.RegisterAdd(entity);

        Assert.Equal(EntityState.Added, context.Entry(entity).State);
    }

    [Fact]
    public void DetachedEditAndDeleteUseExpectedStates()
    {
        using var context = CreateTestContext(":memory:");
        var unitOfWork = new EfUnitOfWorkCore<TestDbContext>(context);
        var edited = new TestEntity();
        var deleted = new TestEntity();

        unitOfWork.RegisterEdit(edited);
        unitOfWork.RegisterDelete(deleted);

        Assert.Equal(EntityState.Modified, context.Entry(edited).State);
        Assert.Equal(EntityState.Deleted, context.Entry(deleted).State);
    }

    private static RoundTableMeetingDbContext CreateContext(string dataSource)
    {
        var options = new DbContextOptionsBuilder<RoundTableMeetingDbContext>()
            .UseSqlite("Data Source=" + dataSource)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .AddInterceptors(new SqliteConnectionInterceptor())
            .Options;
        return new RoundTableMeetingDbContext(options);
    }

    private static TestDbContext CreateTestContext(string dataSource)
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite("Data Source=" + dataSource)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .AddInterceptors(new SqliteConnectionInterceptor())
            .Options;
        return new TestDbContext(options);
    }

    private static async Task<object?> ReadPragmaAsync(DbContext context, string name)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA " + name + ";";
        return await command.ExecuteScalarAsync();
    }

    private sealed class TestEntity : BaseEntity
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }

    private sealed class NonEntity
    {
        public Guid Key { get; set; }
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public SaveMode SaveMode { get; set; }
        public int SaveChangesCalls { get; private set; }
        public TaskCompletionSource SaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DbSet<TestEntity> Entities => Set<TestEntity>();

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveChangesCalls++;
            SaveStarted.TrySetResult();
            if (SaveMode == SaveMode.Block) await ReleaseSave.Task;
            if (SaveMode == SaveMode.Fail) throw new DbUpdateException("save failed");
            if (SaveMode == SaveMode.Concurrency) throw new DbUpdateConcurrencyException("concurrency failed");
            if (SaveMode == SaveMode.Cancel) throw new OperationCanceledException(cancellationToken);
            return await base.SaveChangesAsync(cancellationToken);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TestEntity>().HasKey(entity => entity.ID);
            modelBuilder.Entity<NonEntity>().HasKey(entity => entity.Key);
        }
    }

    private enum SaveMode
    {
        None,
        Block,
        Fail,
        Concurrency,
        Cancel
    }
}
