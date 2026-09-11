using Materal.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.UnitOfWork;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.Repository.Sqlite.Transactions;

/// <summary>取得 SQLite 写锁后读取账号状态，并统一提交或回滚全部账号写入。</summary>
public sealed class AccountTransactionRunner(RoundTableMeetingDbContext dbContext, IUnitOfWork unitOfWork)
    : IAccountTransactionRunner, IScopedDependency<IAccountTransactionRunner>
{
    private bool running;

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (running || dbContext.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Nested account transactions are not supported.");
        running = true;
        IDbContextTransaction? transaction = null;
        SqliteTransaction? sqliteTransaction = null;
        try
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var connection = (SqliteConnection)dbContext.Database.GetDbConnection();
            sqliteTransaction = connection.BeginTransaction(deferred: false);
            transaction = await dbContext.Database.UseTransactionAsync(sqliteTransaction, cancellationToken)
                ?? throw new InvalidOperationException("The account transaction could not be attached.");
            var result = await action(cancellationToken);
            await unitOfWork.CommitAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            if (dbContext.Database.CurrentTransaction is not null)
                await dbContext.Database.UseTransactionAsync(null, CancellationToken.None);
            if (transaction is not null) await transaction.DisposeAsync();
            sqliteTransaction?.Dispose();
            await dbContext.Database.CloseConnectionAsync();
            dbContext.ChangeTracker.Clear();
            running = false;
        }
    }
}
