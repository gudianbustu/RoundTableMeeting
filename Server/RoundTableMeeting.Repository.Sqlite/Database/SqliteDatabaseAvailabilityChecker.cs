using Materal.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RoundTableMeeting.Abstraction.Persistence;

namespace RoundTableMeeting.Repository.Sqlite.Database;

public sealed class SqliteDatabaseAvailabilityChecker : IDatabaseAvailabilityChecker, IScopedDependency<IDatabaseAvailabilityChecker>
{
    private readonly RoundTableMeetingDbContext dbContext;
    private readonly ILogger<SqliteDatabaseAvailabilityChecker> logger;
    private readonly TimeSpan internalTimeout;
    private readonly Func<CancellationToken, Task>? probe;

    public SqliteDatabaseAvailabilityChecker(
        RoundTableMeetingDbContext dbContext,
        ILogger<SqliteDatabaseAvailabilityChecker> logger)
        : this(dbContext, logger, TimeSpan.FromSeconds(2), null)
    {
    }

    internal SqliteDatabaseAvailabilityChecker(
        RoundTableMeetingDbContext dbContext,
        ILogger<SqliteDatabaseAvailabilityChecker> logger,
        TimeSpan internalTimeout,
        Func<CancellationToken, Task>? probe)
    {
        this.dbContext = dbContext;
        this.logger = logger;
        this.internalTimeout = internalTimeout;
        this.probe = probe;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(internalTimeout);
        try
        {
            if (probe is null)
            {
                await dbContext.Database.ExecuteSqlRawAsync("SELECT 1", timeout.Token);
            }
            else
            {
                await probe(timeout.Token);
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("SQLite availability check timed out.");
            return false;
        }
        catch (Exception)
        {
            logger.LogWarning("SQLite availability check failed.");
            return false;
        }
    }
}
