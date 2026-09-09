using System;
using System.Threading;
using System.Threading.Tasks;
using Materal.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Exceptions;
using RoundTableMeeting.Domain.UnitOfWork;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.Repository.Sqlite.Transactions;

public sealed class MeetingTransactionRunner(RoundTableMeetingDbContext dbContext, IUnitOfWork unitOfWork) : IMeetingTransactionRunner, IScopedDependency<IMeetingTransactionRunner>, IScopedDependency, IRegisterType, IRegisterType<IMeetingTransactionRunner>
{
	private bool running;

	public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default(CancellationToken))
	{
		ArgumentNullException.ThrowIfNull(action, "action");
		if (running || dbContext.Database.CurrentTransaction != null)
		{
			throw new InvalidOperationException("Nested meeting transactions are not supported.");
		}
		running = true;
		IDbContextTransaction? transaction = null;
		SqliteTransaction? sqliteTransaction = null;
		T result2;
		try
		{
			await dbContext.Database.OpenConnectionAsync(cancellationToken);
			SqliteConnection connection = (SqliteConnection)dbContext.Database.GetDbConnection();
			sqliteTransaction = connection.BeginTransaction(deferred: false);
			transaction = (await dbContext.Database.UseTransactionAsync(sqliteTransaction, cancellationToken)) ?? throw new InvalidOperationException("The SQLite transaction could not be attached to EF Core.");
			T result = await action(cancellationToken);
			await unitOfWork.CommitAsync(cancellationToken);
			await transaction.CommitAsync(cancellationToken);
			result2 = result;
		}
		catch (DbUpdateException ex) when (IsParticipantUniqueConflict(ex))
		{
			if (transaction != null)
			{
				await transaction.RollbackAsync(CancellationToken.None);
			}
			throw new MeetingJoinConflictException("The meeting participant conflicts with an existing participant.", ex);
		}
		catch (DbUpdateException ex2) when (ex2.InnerException is SqliteException { SqliteErrorCode: 19 } sqlite && sqlite.Message.Contains("MeetingMessages", StringComparison.Ordinal))
		{
			if (transaction != null)
			{
				await transaction.RollbackAsync(CancellationToken.None);
			}
			throw new MessageAppendConflictException("The message submission conflicts with an existing message.", ex2);
		}
		catch (DbUpdateException ex3) when (IsAgentCollaborationConflict(ex3))
		{
			if (transaction != null)
			{
				await transaction.RollbackAsync(CancellationToken.None);
			}
			throw new AgentCollaborationConflictException("The Agent collaboration operation conflicted with existing state.", ex3);
		}
		catch
		{
			if (transaction != null)
			{
				await transaction.RollbackAsync(CancellationToken.None);
			}
			throw;
		}
		finally
		{
			running = false;
			if (dbContext.Database.CurrentTransaction != null)
			{
				await dbContext.Database.UseTransactionAsync(null, CancellationToken.None);
			}
			if (transaction != null)
			{
				await transaction.DisposeAsync();
			}
			sqliteTransaction?.Dispose();
			await dbContext.Database.CloseConnectionAsync();
			dbContext.ChangeTracker.Clear();
		}
		return result2;
	}

	private static bool IsParticipantUniqueConflict(DbUpdateException exception)
	{
		return exception.InnerException is SqliteException { SqliteErrorCode: 19 } ex && (ex.Message.Contains("IX_MeetingParticipants_MeetingId_NormalizedDisplayName", StringComparison.Ordinal) || ex.Message.Contains("IX_MeetingParticipants_MeetingId_UserId", StringComparison.Ordinal) || ex.Message.Contains("MeetingParticipants.MeetingId, MeetingParticipants.NormalizedDisplayName", StringComparison.Ordinal) || ex.Message.Contains("MeetingParticipants.MeetingId, MeetingParticipants.UserId", StringComparison.Ordinal));
	}

	private static bool IsAgentCollaborationConflict(DbUpdateException exception)
	{
		return exception.InnerException is SqliteException { SqliteErrorCode: 19, SqliteExtendedErrorCode: 2067 } ex && (ex.Message.Contains("IX_AgentConnectionSessions_MeetingId_ParticipantId", StringComparison.Ordinal) || ex.Message.Contains("IX_AgentConnectionSessions_McpTransportBinding", StringComparison.Ordinal) || ex.Message.Contains("AgentConnectionSessions.MeetingId, AgentConnectionSessions.ParticipantId", StringComparison.Ordinal) || ex.Message.Contains("AgentConnectionSessions.McpTransportBinding", StringComparison.Ordinal) || ex.Message.Contains("IX_AgentContextCursors_MeetingId_ParticipantId", StringComparison.Ordinal) || ex.Message.Contains("AgentContextCursors.MeetingId, AgentContextCursors.ParticipantId", StringComparison.Ordinal) || ex.Message.Contains("IX_AgentReplyTasks_MeetingId_AgentParticipantId_TriggerMessageId", StringComparison.Ordinal) || ex.Message.Contains("AgentReplyTasks.MeetingId, AgentReplyTasks.AgentParticipantId, AgentReplyTasks.TriggerMessageId", StringComparison.Ordinal) || ex.Message.Contains("IX_AgentReplyTasks_ReplyIdempotencyKey", StringComparison.Ordinal) || ex.Message.Contains("AgentReplyTasks.ReplyIdempotencyKey", StringComparison.Ordinal) || ex.Message.Contains("IX_AgentTaskDeliveryAttempts_TaskId_ID", StringComparison.Ordinal) || ex.Message.Contains("AgentTaskDeliveryAttempts.TaskId, AgentTaskDeliveryAttempts.ID", StringComparison.Ordinal));
	}
}
