using Materal.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Exceptions;
using RoundTableMeeting.Domain.UnitOfWork;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.Repository.Sqlite.Transactions;

public sealed class MeetingTransactionRunner(RoundTableMeetingDbContext dbContext, IUnitOfWork unitOfWork) : IMeetingTransactionRunner, IScopedDependency<IMeetingTransactionRunner>
{
    private bool running;
    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (running || dbContext.Database.CurrentTransaction is not null) throw new InvalidOperationException("Nested meeting transactions are not supported.");
        running = true;
        IDbContextTransaction? transaction = null;
        SqliteTransaction? sqliteTransaction = null;
        try
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
            var connection = (SqliteConnection)dbContext.Database.GetDbConnection();
            sqliteTransaction = connection.BeginTransaction(deferred: false);
            transaction = await dbContext.Database.UseTransactionAsync(sqliteTransaction, cancellationToken)
                ?? throw new InvalidOperationException("The SQLite transaction could not be attached to EF Core.");
            var result = await action(cancellationToken);
            await unitOfWork.CommitAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (DbUpdateException exception) when (IsParticipantUniqueConflict(exception))
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
            throw new MeetingJoinConflictException("The meeting participant conflicts with an existing participant.", exception);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqliteException sqlite && sqlite.SqliteErrorCode == 19 && sqlite.Message.Contains("MeetingMessages", StringComparison.Ordinal))
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
            throw new MessageAppendConflictException("The message submission conflicts with an existing message.", exception);
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            running = false;
            if (dbContext.Database.CurrentTransaction is not null) await dbContext.Database.UseTransactionAsync(null, CancellationToken.None);
            if (transaction is not null) await transaction.DisposeAsync();
            sqliteTransaction?.Dispose();
            await dbContext.Database.CloseConnectionAsync();
            dbContext.ChangeTracker.Clear();
        }
    }

    private static bool IsParticipantUniqueConflict(DbUpdateException exception)
        => exception.InnerException is SqliteException sqlite
            && sqlite.SqliteErrorCode == 19
            && (sqlite.Message.Contains("IX_MeetingParticipants_MeetingId_NormalizedDisplayName", StringComparison.Ordinal)
                || sqlite.Message.Contains("IX_MeetingParticipants_MeetingId_UserId", StringComparison.Ordinal)
                || sqlite.Message.Contains("MeetingParticipants.MeetingId, MeetingParticipants.NormalizedDisplayName", StringComparison.Ordinal)
                || sqlite.Message.Contains("MeetingParticipants.MeetingId, MeetingParticipants.UserId", StringComparison.Ordinal));
}
