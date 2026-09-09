using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Application.AgentCollaboration;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;
using Xunit;

namespace RoundTableMeeting.IntegrationTests;

public sealed class AgentMentionTaskConsumerTests
{
	private sealed record MeetingSeed(Guid MeetingId, Guid HumanId, Guid FirstAgentId, Guid? SecondAgentId);

	private sealed record MessageSeed(Guid MessageId, IReadOnlyList<Guid> MentionIds, MessageAppendedEnvelope Envelope);

	private sealed record MentionInput(Guid ParticipantId, string DisplayName, int StartOffset, int Length);

	[Fact]
	public async Task ConsumerIsRegisteredAndHumanMentionCreatesOneTaskFromTheEarliestMention()
	{
		using TestApplicationFactory factory = new TestApplicationFactory();
		factory.CreateClient();
		MeetingSeed seed = await SeedMeetingAsync(factory, includeSecondAgent: false);
		await using (AsyncServiceScope catalogScope = factory.Services.CreateAsyncScope())
		{
			IMessageEventConsumerCatalog catalog = catalogScope.ServiceProvider.GetRequiredService<IMessageEventConsumerCatalog>();
			Assert.Contains("agent-mention-task-v1", (IEnumerable<string>)catalog.ConsumerNames);
			Assert.Equal<Type>(typeof(AgentMentionTaskConsumer), catalog.GetRequiredServiceType("agent-mention-task-v1"));
		}
		await ConsumeAsync(factory, (await AddMessageAsync(factory, seed, 1L, RoundTableMeeting.Domain.Enums.MessageAuthorKind.Human, "ordinary message")).Envelope);
		await AssertCursorAsync(factory, seed.MeetingId, seed.FirstAgentId, 0L);
		MessageSeed mentioned = await AddMessageAsync(factory, seed, 2L, RoundTableMeeting.Domain.Enums.MessageAuthorKind.Human, "@Analyst and @Analyst", new MentionInput(seed.FirstAgentId, "Analyst", 0, 8), new MentionInput(seed.FirstAgentId, "Analyst", 13, 8));
		await ConsumeAsync(factory, mentioned.Envelope);
		await ConsumeAsync(factory, mentioned.Envelope);
		await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
		RoundTableMeetingDbContext db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
		AgentReplyTask task = await db.AgentReplyTasks.SingleAsync((AgentReplyTask item) => item.MeetingId == seed.MeetingId && item.AgentParticipantId == seed.FirstAgentId);
		Assert.Equal(mentioned.MessageId, task.TriggerMessageId);
		Assert.Equal(mentioned.MentionIds[0], task.TriggerMentionId);
		Assert.Equal(2L, task.TriggerSequence);
		Assert.Equal(0L, task.CandidateFromSequenceExclusive);
		Assert.Equal(2L, task.CandidateToSequenceInclusive);
		Assert.Equal(AgentReplyIdempotencyKeyFactory.Create(task.ID), task.ReplyIdempotencyKey);
		Assert.Equal(2L, (await db.AgentContextCursors.SingleAsync((AgentContextCursor item) => item.MeetingId == seed.MeetingId && item.ParticipantId == seed.FirstAgentId)).AllocatedThroughSequence);
	}

	[Fact]
	public async Task LaterEnvelopeBackfillsEarlierMentionsWithIndependentContinuousAgentRanges()
	{
		using TestApplicationFactory factory = new TestApplicationFactory();
		factory.CreateClient();
		MeetingSeed seed = await SeedMeetingAsync(factory, includeSecondAgent: true);
		Guid secondAgentId = seed.SecondAgentId
			?? throw new InvalidOperationException("The test meeting must include a second agent.");
		MessageSeed first = await AddMessageAsync(factory, seed, 1L, RoundTableMeeting.Domain.Enums.MessageAuthorKind.Human, "@Analyst first", new MentionInput(seed.FirstAgentId, "Analyst", 0, 8));
		await AddMessageAsync(factory, seed, 2L, RoundTableMeeting.Domain.Enums.MessageAuthorKind.System, "System checkpoint");
		await ConsumeAsync(factory, (await AddMessageAsync(factory, seed, 3L, RoundTableMeeting.Domain.Enums.MessageAuthorKind.Human, "@Analyst and @Reviewer", new MentionInput(seed.FirstAgentId, "Analyst", 0, 8), new MentionInput(secondAgentId, "Reviewer", 13, 9))).Envelope);
		await ConsumeAsync(factory, first.Envelope);
		await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
		RoundTableMeetingDbContext db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
		AgentReplyTask[] firstAgentTasks = await (from item in db.AgentReplyTasks.AsNoTracking()
			where item.MeetingId == seed.MeetingId && item.AgentParticipantId == seed.FirstAgentId
			orderby item.TriggerSequence
			select item).ToArrayAsync();
		Assert.Equal(new[] { 1L, 3L }, firstAgentTasks.Select(item => item.TriggerSequence));
		Assert.Equal(new[] { 0L, 1L }, firstAgentTasks.Select(item => item.CandidateFromSequenceExclusive));
		Assert.Equal(new[] { 1L, 3L }, firstAgentTasks.Select(item => item.CandidateToSequenceInclusive));
		AgentReplyTask secondAgentTask = await db.AgentReplyTasks.SingleAsync((AgentReplyTask item) => item.MeetingId == seed.MeetingId && item.AgentParticipantId == secondAgentId);
		Assert.Equal(3L, secondAgentTask.TriggerSequence);
		Assert.Equal(0L, secondAgentTask.CandidateFromSequenceExclusive);
		Assert.Equal(3L, secondAgentTask.CandidateToSequenceInclusive);
		Dictionary<Guid, AgentContextCursor> cursors = await (from item in db.AgentContextCursors.AsNoTracking()
			where item.MeetingId == seed.MeetingId
			select item).ToDictionaryAsync((AgentContextCursor item) => item.ParticipantId);
		Assert.Equal(3L, cursors[seed.FirstAgentId].AllocatedThroughSequence);
		Assert.Equal(3L, cursors[secondAgentId].AllocatedThroughSequence);
	}

	[Fact]
	public async Task NonHumanInactiveAndCrossMeetingMessagesDoNotCreateTasks()
	{
		using TestApplicationFactory factory = new TestApplicationFactory();
		factory.CreateClient();
		MeetingSeed first = await SeedMeetingAsync(factory, includeSecondAgent: true);
		MeetingSeed second = await SeedMeetingAsync(factory, includeSecondAgent: false);
		MessageSeed agentMessage = await AddMessageAsync(factory, first, 1L, RoundTableMeeting.Domain.Enums.MessageAuthorKind.Agent, "@Analyst from agent", new MentionInput(first.FirstAgentId, "Analyst", 0, 8));
		MessageSeed systemMessage = await AddMessageAsync(factory, first, 2L, RoundTableMeeting.Domain.Enums.MessageAuthorKind.System, "@Analyst from system", new MentionInput(first.FirstAgentId, "Analyst", 0, 8));
		await ConsumeAsync(factory, agentMessage.Envelope);
		await ConsumeAsync(factory, systemMessage.Envelope);
		MessageSeed secondMeetingMessage = await AddMessageAsync(factory, second, 1L, RoundTableMeeting.Domain.Enums.MessageAuthorKind.Human, "@Analyst in another meeting", new MentionInput(second.FirstAgentId, "Analyst", 0, 8));
		await ConsumeAsync(factory, secondMeetingMessage.Envelope);
		await ConsumeAsync(factory, secondMeetingMessage.Envelope with
		{
			MeetingId = first.MeetingId
		});
		MessageSeed deletingMessage = await AddMessageAsync(factory, first, 3L, RoundTableMeeting.Domain.Enums.MessageAuthorKind.Human, "@Analyst while deleting", new MentionInput(first.FirstAgentId, "Analyst", 0, 8));
		await BeginDeletingAsync(factory, first.MeetingId);
		await ConsumeAsync(factory, deletingMessage.Envelope);
		await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
		RoundTableMeetingDbContext db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
		Assert.False(await db.AgentReplyTasks.AnyAsync((AgentReplyTask item) => item.MeetingId == first.MeetingId));
		Assert.Equal(0L, (await db.AgentContextCursors.SingleAsync((AgentContextCursor item) => item.MeetingId == first.MeetingId && item.ParticipantId == first.FirstAgentId)).AllocatedThroughSequence);
	}

	[Fact]
	public async Task ConcurrentConsumersCreateOneOrderedTaskPerMentionWithoutCursorRegression()
	{
		TestApplicationFactory factory = new TestApplicationFactory();
		try
		{
			factory.CreateClient();
			MeetingSeed seed = await SeedMeetingAsync(factory, includeSecondAgent: false);
			await AddMessageAsync(factory, seed, 1L, RoundTableMeeting.Domain.Enums.MessageAuthorKind.Human, "@Analyst first", new MentionInput(seed.FirstAgentId, "Analyst", 0, 8));
			MessageSeed second = await AddMessageAsync(factory, seed, 2L, RoundTableMeeting.Domain.Enums.MessageAuthorKind.Human, "@Analyst second", new MentionInput(seed.FirstAgentId, "Analyst", 0, 8));
			await Task.WhenAll(from _ in Enumerable.Range(0, 4)
				select ConsumeAsync(factory, second.Envelope));
			await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
			RoundTableMeetingDbContext db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
			AgentReplyTask[] tasks = await (from item in db.AgentReplyTasks.AsNoTracking()
				where item.MeetingId == seed.MeetingId && item.AgentParticipantId == seed.FirstAgentId
				orderby item.TriggerSequence
				select item).ToArrayAsync();
			Assert.Equal(new[] { 1L, 2L }, tasks.Select(item => item.TriggerSequence));
			Assert.Equal(new[] { 0L, 1L }, tasks.Select(item => item.CandidateFromSequenceExclusive));
			Assert.Equal(2L, (await db.AgentContextCursors.SingleAsync((AgentContextCursor item) => item.MeetingId == seed.MeetingId && item.ParticipantId == seed.FirstAgentId)).AllocatedThroughSequence);
		}
		finally
		{
			if (factory != null)
			{
				((IDisposable)factory).Dispose();
			}
		}
	}

	private static async Task<MeetingSeed> SeedMeetingAsync(TestApplicationFactory factory, bool includeSecondAgent)
	{
		MeetingSeed result;
		await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
		{
			RoundTableMeetingDbContext db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
			DateTimeOffset now = DateTimeOffset.UtcNow.AddMinutes(-5.0);
			UserAccount user = UserAccount.Create($"consumer-{Guid.NewGuid():N}", "Owner");
			user.InitializePasswordHash("hash");
			Meeting meeting = Meeting.Create("Consumer test", null, user.ID, now);
			MeetingParticipant human = MeetingParticipant.CreateHuman(meeting.ID, user.ID, "Alice", now);
			MeetingParticipant firstAgent = MeetingParticipant.CreateAgent(meeting.ID, "Analyst", now);
			MeetingParticipant? secondAgent = includeSecondAgent ? MeetingParticipant.CreateAgent(meeting.ID, "Reviewer", now) : null;
			AgentContextCursor firstCursor = AgentContextCursor.Create(meeting.ID, firstAgent.ID, 0L, now);
			db.AddRange(user, meeting, human, firstAgent, firstCursor);
			if (secondAgent != null)
			{
				db.Add(secondAgent);
				db.Add(AgentContextCursor.Create(meeting.ID, secondAgent.ID, 0L, now));
			}
			await db.SaveChangesAsync();
			result = new MeetingSeed(meeting.ID, human.ID, firstAgent.ID, secondAgent?.ID);
		}
		return result;
	}

	private static async Task<MessageSeed> AddMessageAsync(TestApplicationFactory factory, MeetingSeed seed, long sequence, RoundTableMeeting.Domain.Enums.MessageAuthorKind authorKind, string content, params MentionInput[] mentions)
	{
		MessageSeed result;
		await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
		{
			RoundTableMeetingDbContext db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
			DateTimeOffset now = DateTimeOffset.UtcNow.AddMinutes(-4.0).AddSeconds(sequence);
			MeetingMessage meetingMessage = authorKind switch
			{
				RoundTableMeeting.Domain.Enums.MessageAuthorKind.System => MeetingMessage.CreateSystem(seed.MeetingId, sequence, "System", content, Guid.NewGuid(), now),
				RoundTableMeeting.Domain.Enums.MessageAuthorKind.Agent => MeetingMessage.CreateParticipant(seed.MeetingId, sequence, seed.SecondAgentId ?? seed.FirstAgentId, RoundTableMeeting.Domain.Enums.MessageAuthorKind.Agent, seed.SecondAgentId.HasValue ? "Reviewer" : "Analyst", content, Guid.NewGuid(), now),
				_ => MeetingMessage.CreateParticipant(seed.MeetingId, sequence, seed.HumanId, RoundTableMeeting.Domain.Enums.MessageAuthorKind.Human, "Alice", content, Guid.NewGuid(), now),
			};
			MeetingMessage message = meetingMessage;
			MessageMention[] mentionEntities = mentions.Select((MentionInput mention) => MessageMention.Create(seed.MeetingId, message.ID, mention.ParticipantId, mention.DisplayName, mention.StartOffset, mention.Length)).ToArray();
			db.Add(message);
			db.AddRange(mentionEntities);
			await db.SaveChangesAsync();
			result = new MessageSeed(message.ID, mentionEntities.Select((MessageMention item) => item.ID).ToArray(), new MessageAppendedEnvelope(Guid.NewGuid(), message.ID, seed.MeetingId, sequence, now));
		}
		return result;
	}

	private static async Task ConsumeAsync(TestApplicationFactory factory, MessageAppendedEnvelope envelope)
	{
		await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
		await scope.ServiceProvider.GetRequiredService<AgentMentionTaskConsumer>().ConsumeAsync(envelope);
	}

	private static async Task BeginDeletingAsync(TestApplicationFactory factory, Guid meetingId)
	{
		await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
		RoundTableMeetingDbContext db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
		Meeting meeting = await db.Meetings.SingleAsync((Meeting item) => item.ID == meetingId);
		meeting.BeginDeleting();
		db.Update(meeting);
		await db.SaveChangesAsync();
	}

	private static async Task AssertCursorAsync(TestApplicationFactory factory, Guid meetingId, Guid participantId, long expectedAllocated)
	{
		await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
		RoundTableMeetingDbContext db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
		Assert.Equal(expectedAllocated, (await db.AgentContextCursors.SingleAsync((AgentContextCursor item) => item.MeetingId == meetingId && item.ParticipantId == participantId)).AllocatedThroughSequence);
	}
}
