using Materal.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RoundTableMeeting.Abstraction.Persistence;
using RoundTableMeeting.Repository.Sqlite.Configuration;

namespace RoundTableMeeting.Repository.Sqlite.Database;

/// <summary>
/// 初始化 SQLite 数据库并校验关键参数
/// </summary>
public sealed class SqliteDatabaseInitializer(
    RoundTableMeetingDbContext dbContext,
    IOptions<SqliteDatabaseOptions> options,
    ILogger<SqliteDatabaseInitializer> logger)
    : IDatabaseInitializer, IScopedDependency<IDatabaseInitializer>
{
    /// <summary>
    /// 创建目录、应用迁移并校验 SQLite 参数
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var normalizedConnectionString = SqlitePathResolver.NormalizeConnectionString(options.Value.ConnectionString);
        Directory.CreateDirectory(SqlitePathResolver.GetDatabaseDirectory(normalizedConnectionString));

        await dbContext.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = dbContext.Database.GetDbConnection();
            await using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode = WAL;";
                var journalMode = Convert.ToString(await pragma.ExecuteScalarAsync(cancellationToken));
                if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("SQLite WAL mode could not be enabled.");
                }
            }

            await using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout;";
                var busyTimeout = Convert.ToInt32(await pragma.ExecuteScalarAsync(cancellationToken));
                if (busyTimeout != 5000)
                {
                    throw new InvalidOperationException("SQLite busy timeout could not be configured.");
                }
            }

            await dbContext.Database.MigrateAsync(cancellationToken);
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
            SqliteConnection.ClearAllPools();
        }

        // 打开全新上下文，独立校验已持久化的 pragma
        var verificationOptions = new DbContextOptionsBuilder<RoundTableMeetingDbContext>()
            .UseSqlite(normalizedConnectionString, sqlite => sqlite.MigrationsAssembly(typeof(RoundTableMeetingDbContext).Assembly.FullName))
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .AddInterceptors(new SqliteConnectionInterceptor())
            .Options;
        await using var verificationContext = new RoundTableMeetingDbContext(verificationOptions);
        await verificationContext.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = verificationContext.Database.GetDbConnection();
            await using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode;";
                var journalMode = Convert.ToString(await pragma.ExecuteScalarAsync(cancellationToken));
                if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("SQLite WAL mode was not retained.");
                }
            }

            await using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout;";
                var busyTimeout = Convert.ToInt32(await pragma.ExecuteScalarAsync(cancellationToken));
                if (busyTimeout != 5000)
                {
                    throw new InvalidOperationException("SQLite busy timeout was not retained.");
                }
            }
        }
        finally
        {
            await verificationContext.Database.CloseConnectionAsync();
            SqliteConnection.ClearAllPools();
        }
        logger.LogInformation("SQLite database initialization completed.");
    }
}
