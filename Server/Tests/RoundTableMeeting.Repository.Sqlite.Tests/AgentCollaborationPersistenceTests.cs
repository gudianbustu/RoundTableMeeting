using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;
using RoundTableMeeting.Repository.Sqlite.Repositories;
using RoundTableMeeting.Repository.Sqlite.Transactions;
using RoundTableMeeting.Repository.Sqlite.UnitOfWork;
using Xunit;

namespace RoundTableMeeting.Repository.Sqlite.Tests;

public sealed class AgentCollaborationPersistenceTests
{
	private sealed record Seed(Guid MeetingId, Guid AgentParticipantId, Guid SourceParticipantId, Guid TriggerMessageId, long TriggerSequence, Guid TriggerMentionId, Guid ConnectionSessionId, Guid TaskId, Guid ReplyIdempotencyKey, Guid DeliveryAttemptId);

	private static readonly DateTimeOffset BaseTime = DateTimeOffset.Parse("2026-09-04T00:00:00Z");

	[Fact]
	public async Task MigrationCreatesAgentTablesAndPreservesThemeFourData()
	{
		string root = CreateRoot();
		string path = Path.Combine(root, "migration.db");
		try
		{
			await using RoundTableMeetingDbContext context = CreateContext(path);
			await context.Database.MigrateAsync("20260901074149_AddRealtimeChat");
			UserAccount user = UserAccount.Create("migration-owner", "Owner");
			user.InitializePasswordHash("hash");
			Meeting meeting = Meeting.Create("Migration", null, user.ID, BaseTime);
			MeetingParticipant participant = MeetingParticipant.CreateHuman(meeting.ID, user.ID, "Owner", BaseTime);
			MeetingMessage message = MeetingMessage.CreateParticipant(meeting.ID, 1L, participant.ID, MessageAuthorKind.Human, "Owner", "hello", Guid.NewGuid(), BaseTime);
			await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO UserAccounts (ID, Username, NormalizedUsername, Nickname, PasswordHash) VALUES ({user.ID}, {user.Username}, {user.NormalizedUsername}, {user.Nickname}, {user.PasswordHash})");
            context.Add(meeting);
            await context.SaveChangesAsync();
            await LegacyMeetingParticipantSeed.InsertAsync(context, participant);
            context.Add(message);
			await context.SaveChangesAsync();
			await context.Database.MigrateAsync();
			Assert.Equal(1, await context.MeetingMessages.CountAsync());
			string[] array = new string[4] { "AgentConnectionSessions", "AgentContextCursors", "AgentReplyTasks", "AgentTaskDeliveryAttempts" };
			foreach (string table in array)
			{
				Assert.Equal(1L, await context.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = {0}", new object[1] { table }).SingleAsync());
			}
		}
		finally
		{
			Cleanup(root);
		}
	}

	[Fact]
	public async Task MigrationCanRollBackAgentTablesWithoutLosingThemeFourData()
	{
		string root = CreateRoot();
		try
		{
			await using RoundTableMeetingDbContext context = CreateContext(Path.Combine(root, "rollback.db"));
			await context.Database.MigrateAsync();
			Seed seed = await SeedAsync(context, "rollback");
			await context.Database.MigrateAsync("20260901074149_AddRealtimeChat");
			Assert.Equal(1, await context.MeetingMessages.CountAsync((MeetingMessage message) => message.ID == seed.TriggerMessageId));
			string[] array = new string[4] { "AgentConnectionSessions", "AgentContextCursors", "AgentReplyTasks", "AgentTaskDeliveryAttempts" };
			foreach (string table in array)
			{
				Assert.Equal(0L, await context.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = {0}", new object[1] { table }).SingleAsync());
			}
		}
		finally
		{
			Cleanup(root);
		}
	}

	[Fact]
	public async Task AgentRepositoryIsMeetingScopedFifoOrderedAndNoTracking()
	{
		string root = CreateRoot();
		try
		{
			RoundTableMeetingDbContext context = CreateContext(Path.Combine(root, "repository.db"));
			try
			{
				await context.Database.MigrateAsync();
				Seed seed = await SeedAsync(context, "repository");
				Seed second = await AddSecondTaskAsync(context, seed);
				AgentCollaborationRepository repository = new AgentCollaborationRepository(context);
				AgentConnectionSession? connection = await repository.FindConnectedConnectionAsync(seed.MeetingId, seed.AgentParticipantId);
				Assert.NotNull(connection);
				Assert.Equal(EntityState.Detached, context.Entry(connection).State);
				Assert.Null(await repository.FindConnectedConnectionAsync(Guid.NewGuid(), seed.AgentParticipantId));
				IReadOnlyList<AgentReplyTask> tasks = await repository.ListTasksAsync(seed.MeetingId, seed.AgentParticipantId);
				Assert.Equal(new Guid[2] { seed.TaskId, second.TaskId }, tasks.Select((AgentReplyTask task) => task.ID));
				Assert.All(tasks, delegate(AgentReplyTask task)
				{
					Assert.Equal(EntityState.Detached, context.Entry(task).State);
				});
				IReadOnlyList<AgentTaskDeliveryAttempt> attempts = await repository.ListDeliveryAttemptsAsync(seed.MeetingId, seed.TaskId);
				Assert.Single(attempts);
				Assert.Equal(seed.DeliveryAttemptId, attempts[0].ID);
				Assert.NotNull(await repository.FindDeliveryAttemptAsync(seed.MeetingId, seed.TaskId, seed.DeliveryAttemptId));
				Assert.Null(await repository.FindDeliveryAttemptAsync(seed.MeetingId, second.TaskId, seed.DeliveryAttemptId));
			}
			finally
			{
				if (context != null)
				{
					await context.DisposeAsync();
				}
			}
		}
		finally
		{
			Cleanup(root);
		}
	}

	[Fact]
	public async Task AgentEnumsPersistAsFrozenStringValues()
	{
		string root = CreateRoot();
		try
		{
			await using RoundTableMeetingDbContext context = CreateContext(Path.Combine(root, "enum-strings.db"));
			await context.Database.MigrateAsync();
			Seed seed = await SeedAsync(context, "enum-strings");
			Assert.Equal("Connected", await context.Database.SqlQueryRaw<string>("SELECT State AS Value FROM AgentConnectionSessions WHERE ID = {0}", new object[1] { seed.ConnectionSessionId }).SingleAsync());
			Assert.Equal("Prepared", await context.Database.SqlQueryRaw<string>("SELECT State AS Value FROM AgentInitialContextDeliveries WHERE ConnectionSessionId = {0}", new object[1] { seed.ConnectionSessionId }).SingleAsync());
			Assert.Equal("Queued", await context.Database.SqlQueryRaw<string>("SELECT State AS Value FROM AgentReplyTasks WHERE ID = {0}", new object[1] { seed.TaskId }).SingleAsync());
			Assert.Equal("Human", await context.Database.SqlQueryRaw<string>("SELECT SourceAuthorKind AS Value FROM AgentReplyTasks WHERE ID = {0}", new object[1] { seed.TaskId }).SingleAsync());
			Assert.Equal("Prepared", await context.Database.SqlQueryRaw<string>("SELECT State AS Value FROM AgentTaskDeliveryAttempts WHERE ID = {0}", new object[1] { seed.DeliveryAttemptId }).SingleAsync());
		}
		finally
		{
			Cleanup(root);
		}
	}

	[Fact]
	public async Task UniqueKeysPreventDuplicateActiveConnectionTaskAndReplyIdempotencyKey()
	{
		string root = CreateRoot();
		try
		{
			RoundTableMeetingDbContext context = CreateContext(Path.Combine(root, "unique.db"));
			try
			{
				await context.Database.MigrateAsync();
				Seed seed = await SeedAsync(context, "unique");
				context.Add(AgentConnectionSession.Create(seed.MeetingId, seed.AgentParticipantId, 12000, "another-binding", BaseTime.AddSeconds(10.0)));
				await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
				context.ChangeTracker.Clear();
				AgentReplyTask duplicateTask = AgentReplyTask.Create(Guid.NewGuid(), seed.MeetingId, seed.AgentParticipantId, seed.TriggerMessageId, 1L, seed.TriggerMentionId, MessageAuthorKind.Human, seed.SourceParticipantId, 0L, Guid.NewGuid(), BaseTime.AddSeconds(11.0));
				context.Add(duplicateTask);
				await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
				context.ChangeTracker.Clear();
				Seed second = await AddSecondTaskAsync(context, seed);
				AgentReplyTask duplicateReplyKey = AgentReplyTask.Create(Guid.NewGuid(), seed.MeetingId, seed.AgentParticipantId, second.TriggerMessageId, second.TriggerSequence, second.TriggerMentionId, MessageAuthorKind.Human, seed.SourceParticipantId, 1L, seed.ReplyIdempotencyKey, BaseTime.AddSeconds(12.0));
				context.Add(duplicateReplyKey);
				await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
			}
			finally
			{
				if (context != null)
				{
					await context.DisposeAsync();
				}
			}
		}
		finally
		{
			Cleanup(root);
		}
	}

	[Fact]
	public async Task DisconnectConnectionRequiresExactMeetingParticipantConnectionAndBinding()
	{
		string root = CreateRoot();
		try
		{
			await using RoundTableMeetingDbContext context = CreateContext(Path.Combine(root, "disconnect-scope.db"));
			await context.Database.MigrateAsync();
			Seed seed = await SeedAsync(context, "disconnect-scope");
			AgentCollaborationRepository repository = new AgentCollaborationRepository(context);
			DateTimeOffset disconnectedAt = BaseTime.AddSeconds(1.0);
			Assert.False(await repository.DisconnectConnectionAsync(Guid.NewGuid(), seed.AgentParticipantId, seed.ConnectionSessionId, "binding-disconnect-scope", disconnectedAt));
			Assert.False(await repository.DisconnectConnectionAsync(seed.MeetingId, Guid.NewGuid(), seed.ConnectionSessionId, "binding-disconnect-scope", disconnectedAt));
			Assert.False(await repository.DisconnectConnectionAsync(seed.MeetingId, seed.AgentParticipantId, Guid.NewGuid(), "binding-disconnect-scope", disconnectedAt));
			Assert.False(await repository.DisconnectConnectionAsync(seed.MeetingId, seed.AgentParticipantId, seed.ConnectionSessionId, "other-binding", disconnectedAt));
			Assert.True(await repository.DisconnectConnectionAsync(seed.MeetingId, seed.AgentParticipantId, seed.ConnectionSessionId, "binding-disconnect-scope", disconnectedAt));
			AgentConnectionSession connection = await context.AgentConnectionSessions.SingleAsync((AgentConnectionSession item) => item.ID == seed.ConnectionSessionId);
			Assert.Equal(AgentConnectionState.Disconnected, connection.State);
			Assert.Equal(disconnectedAt, connection.DisconnectedAtUtc);
			Assert.Equal(disconnectedAt, connection.LastActivityAtUtc);
		}
		finally
		{
			Cleanup(root);
		}
	}

	[Fact]
	public async Task DisconnectConnectionRejectsOlderTimestampWithoutMovingLastActivityBackwards()
	{
		string root = CreateRoot();
		try
		{
			await using RoundTableMeetingDbContext context = CreateContext(Path.Combine(root, "disconnect-time.db"));
			await context.Database.MigrateAsync();
			Seed seed = await SeedAsync(context, "disconnect-time");
			AgentCollaborationRepository repository = new AgentCollaborationRepository(context);
			Assert.False(await repository.DisconnectConnectionAsync(seed.MeetingId, seed.AgentParticipantId, seed.ConnectionSessionId, "binding-disconnect-time", BaseTime.AddSeconds(-1.0)));
			AgentConnectionSession connected = await context.AgentConnectionSessions.SingleAsync((AgentConnectionSession item) => item.ID == seed.ConnectionSessionId);
			Assert.Equal(AgentConnectionState.Connected, connected.State);
			Assert.Equal(BaseTime, connected.LastActivityAtUtc);
			Assert.True(await repository.DisconnectConnectionAsync(seed.MeetingId, seed.AgentParticipantId, seed.ConnectionSessionId, "binding-disconnect-time", BaseTime.AddSeconds(1.0)));
			Assert.Equal(actual: (await context.AgentConnectionSessions.SingleAsync((AgentConnectionSession item) => item.ID == seed.ConnectionSessionId)).LastActivityAtUtc, expected: BaseTime.AddSeconds(1.0));
		}
		finally
		{
			Cleanup(root);
		}
	}

	[Fact]
	public async Task MeetingTransactionRunnerDoesNotMapAgentCheckConstraintToAgentConflict()
	{
		string root = CreateRoot();
		try
		{
			await using RoundTableMeetingDbContext context = CreateContext(Path.Combine(root, "transaction-check.db"));
			await context.Database.MigrateAsync();
			Seed seed = await SeedAsync(context, "transaction-check");
			context.ChangeTracker.Clear();
			UnitOfWorkImpl unitOfWork = new UnitOfWorkImpl(context);
			MeetingTransactionRunner runner = new MeetingTransactionRunner(context, unitOfWork);
			MeetingParticipant agent = MeetingParticipant.CreateAgent(seed.MeetingId, "Check Agent", BaseTime.AddSeconds(1.0));
			AgentConnectionSession connection = AgentConnectionSession.Create(seed.MeetingId, agent.ID, 12000, "binding-transaction-check", BaseTime.AddSeconds(1.0));
			context.Entry(connection).Property((AgentConnectionSession item) => item.State).CurrentValue = (AgentConnectionState)999;
			await Assert.ThrowsAsync<DbUpdateException>(() => runner.RunAsync(delegate
			{
				unitOfWork.RegisterAdd(agent);
				unitOfWork.RegisterAdd(connection);
				return Task.FromResult(result: true);
			}));
			Assert.Null(await context.AgentConnectionSessions.SingleOrDefaultAsync((AgentConnectionSession item) => item.ID == connection.ID));
		}
		finally
		{
			Cleanup(root);
		}
	}

	[Fact]
	public async Task CompositeForeignKeysRejectCrossMeetingAgentReferences()
	{
		string root = CreateRoot();
		try
		{
			RoundTableMeetingDbContext context = CreateContext(Path.Combine(root, "foreign-keys.db"));
			try
			{
				await context.Database.MigrateAsync();
				Seed first = await SeedAsync(context, "first");
				Seed second = await SeedAsync(context, "second");
				await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AgentConnectionSessions\r\n(ID, MeetingId, ParticipantId, Transport, State, DeclaredContextCharacterLimit,\r\n McpTransportBinding, ConnectedAtUtc,\r\n LastActivityAtUtc, CreatedAtUtc)\r\nVALUES ({Guid.NewGuid()}, {second.MeetingId}, {first.AgentParticipantId},\r\n        'mcp-streamable-http', 'Connected', 12000, 'cross-binding',\r\n        {BaseTime}, {BaseTime}, {BaseTime})"));
				await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AgentContextCursors\r\n(ID, MeetingId, ParticipantId, DeliveredThroughSequence, AllocatedThroughSequence, UpdatedAtUtc)\r\nVALUES ({Guid.NewGuid()}, {second.MeetingId}, {first.AgentParticipantId}, 0, 0, {BaseTime})"));
				await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AgentReplyTasks\r\n(ID, MeetingId, AgentParticipantId, TriggerMessageId, TriggerSequence, TriggerMentionId,\r\n SourceAuthorKind, CandidateFromSequenceExclusive, CandidateToSequenceInclusive, State,\r\n ReplyIdempotencyKey, CreatedAtUtc)\r\nVALUES ({Guid.NewGuid()}, {second.MeetingId}, {first.AgentParticipantId}, {first.TriggerMessageId},\r\n        1, {first.TriggerMentionId}, 'System', 0, 1, 'Queued', {Guid.NewGuid()}, {BaseTime})"));
				await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AgentTaskDeliveryAttempts\r\n(ID, TaskId, ConnectionSessionId, MeetingId, CandidateFirstSequence, CandidateLastSequence,\r\n DeclaredContextCharacterLimit, DeliveredCharacterCount, Truncated, OverLimit,\r\n TruncatedMessageCount, State, CreatedAtUtc)\r\nVALUES ({Guid.NewGuid()}, {first.TaskId}, {first.ConnectionSessionId}, {second.MeetingId},\r\n        1, 1, 12000, 0, 0, 0, 0, 'Prepared', {BaseTime})"));
			}
			finally
			{
				if (context != null)
				{
					await context.DisposeAsync();
				}
			}
		}
		finally
		{
			Cleanup(root);
		}
	}

	[Fact]
	public async Task AgentCheckConstraintsRejectInvalidStatesRangesAndFieldCombinations()
	{
		string root = CreateRoot();
		try
		{
			RoundTableMeetingDbContext context = CreateContext(Path.Combine(root, "checks.db"));
			try
			{
				await context.Database.MigrateAsync();
				Seed seed = await SeedAsync(context, "checks");
				await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AgentConnectionSessions\r\n(ID, MeetingId, ParticipantId, Transport, State, DeclaredContextCharacterLimit,\r\n McpTransportBinding, ConnectedAtUtc,\r\n LastActivityAtUtc, CreatedAtUtc)\r\nVALUES ({Guid.NewGuid()}, {seed.MeetingId}, {seed.AgentParticipantId},\r\n        'mcp-streamable-http', 'Invalid', 12000, 'invalid-state',\r\n        {BaseTime}, {BaseTime}, {BaseTime})"));
				await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AgentContextCursors\r\n(ID, MeetingId, ParticipantId, DeliveredThroughSequence, AllocatedThroughSequence, UpdatedAtUtc)\r\nVALUES ({Guid.NewGuid()}, {seed.MeetingId}, {seed.AgentParticipantId}, 2, 1, {BaseTime})"));
				await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AgentReplyTasks\r\n(ID, MeetingId, AgentParticipantId, TriggerMessageId, TriggerSequence, TriggerMentionId,\r\n SourceAuthorKind, SourceParticipantId, CandidateFromSequenceExclusive,\r\n CandidateToSequenceInclusive, State, ReplyIdempotencyKey, CreatedAtUtc)\r\nVALUES ({Guid.NewGuid()}, {seed.MeetingId}, {seed.AgentParticipantId}, {seed.TriggerMessageId},\r\n        1, {seed.TriggerMentionId}, 'Human', {seed.SourceParticipantId}, 1, 1,\r\n        'Queued', {Guid.NewGuid()}, {BaseTime})"));
				await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AgentTaskDeliveryAttempts\r\n(ID, TaskId, ConnectionSessionId, MeetingId, CandidateFirstSequence, CandidateLastSequence,\r\n DeliveredFirstSequence, DeliveredLastSequence, DeclaredContextCharacterLimit,\r\n DeliveredCharacterCount, Truncated, OverLimit, TruncatedMessageCount, State, CreatedAtUtc)\r\nVALUES ({Guid.NewGuid()}, {seed.TaskId}, {seed.ConnectionSessionId}, {seed.MeetingId},\r\n        1, 3, 1, 2, 12000, 10, 0, 0, 0, 'Prepared', {BaseTime})"));
			}
			finally
			{
				if (context != null)
				{
					await context.DisposeAsync();
				}
			}
		}
		finally
		{
			Cleanup(root);
		}
	}

	private static async Task<Seed> SeedAsync(RoundTableMeetingDbContext context, string suffix)
	{
		UserAccount user = UserAccount.Create($"owner-{suffix}-{Guid.NewGuid():N}", "Owner");
		user.InitializePasswordHash("hash");
		Meeting meeting = Meeting.Create("Meeting " + suffix, null, user.ID, BaseTime);
		MeetingParticipant human = MeetingParticipant.CreateHuman(meeting.ID, user.ID, "Alice", BaseTime);
		MeetingParticipant agent = MeetingParticipant.CreateAgent(meeting.ID, "Analyst", BaseTime);
		MeetingMessage message = MeetingMessage.CreateParticipant(meeting.ID, 1L, human.ID, MessageAuthorKind.Human, human.DisplayName, "@Analyst please review", Guid.NewGuid(), BaseTime);
		MessageMention mention = MessageMention.Create(meeting.ID, message.ID, agent.ID, agent.DisplayName, 0, 8);
		AgentConnectionSession connection = AgentConnectionSession.Create(meeting.ID, agent.ID, 12000, "binding-" + suffix, BaseTime);
		AgentContextCursor cursor = AgentContextCursor.Create(meeting.ID, agent.ID, 0L, BaseTime);
		AgentReplyTask task = AgentReplyTask.Create(Guid.NewGuid(), meeting.ID, agent.ID, message.ID, message.Sequence, mention.ID, MessageAuthorKind.Human, human.ID, 0L, Guid.NewGuid(), BaseTime);
		AgentTaskDeliveryAttempt attempt = AgentTaskDeliveryAttempt.Create(task.ID, connection.ID, meeting.ID, 1L, 1L, 12000, 24, truncated: false, overLimit: false, 0, BaseTime);
		context.AddRange(user, meeting, human, agent, message, mention, connection, cursor, AgentCollaborationBoundary.Create(meeting.ID));
		await context.SaveChangesAsync();
		context.AddRange(task, attempt);
        context.Add(AgentInitialContextDelivery.Create(meeting.ID, agent.ID, connection.ID,
            new(0, null, null, null, null, 12000, 0, false, false, 0, "agent-context-v1"), BaseTime));
		await context.SaveChangesAsync();
		return new Seed(meeting.ID, agent.ID, human.ID, message.ID, 1L, mention.ID, connection.ID, task.ID, task.ReplyIdempotencyKey, attempt.ID);
	}

	private static async Task<Seed> AddSecondTaskAsync(RoundTableMeetingDbContext context, Seed seed)
	{
		MeetingMessage message = MeetingMessage.CreateParticipant(seed.MeetingId, 2L, seed.SourceParticipantId, MessageAuthorKind.Human, "Alice", "@Analyst follow up", Guid.NewGuid(), BaseTime.AddSeconds(5.0));
		MessageMention mention = MessageMention.Create(seed.MeetingId, message.ID, seed.AgentParticipantId, "Analyst", 0, 8);
		AgentReplyTask task = AgentReplyTask.Create(Guid.NewGuid(), seed.MeetingId, seed.AgentParticipantId, message.ID, message.Sequence, mention.ID, MessageAuthorKind.Human, seed.SourceParticipantId, 1L, Guid.NewGuid(), BaseTime.AddSeconds(5.0));
		context.AddRange(message, mention, task);
		await context.SaveChangesAsync();
		return seed with
		{
			TriggerMessageId = message.ID,
			TriggerSequence = message.Sequence,
			TriggerMentionId = mention.ID,
			TaskId = task.ID,
			ReplyIdempotencyKey = task.ReplyIdempotencyKey
		};
	}

	private static RoundTableMeetingDbContext CreateContext(string path)
	{
		return new RoundTableMeetingDbContext(new DbContextOptionsBuilder<RoundTableMeetingDbContext>().UseSqlite("Data Source=" + path + ";Foreign Keys=True;Pooling=False").UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking).Options);
	}

	private static string CreateRoot()
	{
		string text = Path.Combine(Path.GetTempPath(), "RoundTableMeetingAgentTests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(text);
		return text;
	}

	private static void Cleanup(string root)
	{
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(root))
		{
			Directory.Delete(root, recursive: true);
		}
	}
}
