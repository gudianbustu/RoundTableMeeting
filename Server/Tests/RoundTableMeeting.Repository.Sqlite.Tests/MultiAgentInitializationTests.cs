using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RoundTableMeeting.Repository.Sqlite.Configuration;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.Repository.Sqlite.Tests;

public sealed partial class MultiAgentCollaborationPersistenceTests
{
    [Fact]
    public async Task PreopenedConnectionWithDisabledForeignKeysIsRejectedBeforeMigration()
    {
        await using var db = Open();
        await db.Database.OpenConnectionAsync(cancellationToken: TestContext.Current.CancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;", cancellationToken: TestContext.Current.CancellationToken);
        var initializer = new SqliteDatabaseInitializer(db,
            Options.Create(new SqliteDatabaseOptions { ConnectionString = $"Data Source={path};Foreign Keys=True;Pooling=False" }),
            NullLogger<SqliteDatabaseInitializer>.Instance);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => initializer.InitializeAsync(TestContext.Current.CancellationToken));
        Assert.Contains("before migration", failure.Message);
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>(
            "SELECT count(*) AS Value FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'").SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
    }
}
