using Materal.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RoundTableMeeting.Abstraction.Persistence;

namespace RoundTableMeeting.Repository.Sqlite.Database;

/// <summary>
/// 探测 SQLite 数据库是否可用
/// </summary>
public sealed class SqliteDatabaseAvailabilityChecker : IDatabaseAvailabilityChecker, IScopedDependency<IDatabaseAvailabilityChecker>
{
    /// <summary>
    /// 用于数据库操作的 EF Core 上下文
    /// </summary>
    private readonly RoundTableMeetingDbContext dbContext;
    /// <summary>
    /// 用于记录探测异常的日志器
    /// </summary>
    private readonly ILogger<SqliteDatabaseAvailabilityChecker> logger;
    /// <summary>
    /// 数据库健康探测的内部超时
    /// </summary>
    private readonly TimeSpan internalTimeout;
    /// <summary>
    /// 供专项测试注入的数据库探测委托
    /// </summary>
    private readonly Func<CancellationToken, Task>? probe;

    /// <summary>
    /// 使用两秒内部超时初始化生产探测器
    /// </summary>
    public SqliteDatabaseAvailabilityChecker(
        RoundTableMeetingDbContext dbContext,
        ILogger<SqliteDatabaseAvailabilityChecker> logger)
        : this(dbContext, logger, TimeSpan.FromSeconds(2), null)
    {
    }

    /// <summary>
    /// 初始化供专项测试注入的数据库探测器
    /// </summary>
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

    /// <summary>
    /// 返回数据库可用性探测结果
    /// </summary>
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
