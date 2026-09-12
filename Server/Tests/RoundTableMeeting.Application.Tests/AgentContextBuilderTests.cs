using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;
using RoundTableMeeting.Application.AgentCollaboration;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.AgentCollaboration;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Domain.Repositories;
using Xunit;

namespace RoundTableMeeting.Application.Tests;

public sealed class AgentContextBuilderTests
{
    [Fact]
    public async Task FrozenReplayPreservesWindowDespiteNewMessagesAndRejectsMissingHistory()
    {
        var meeting = Guid.NewGuid();
        var older = MeetingMessage.CreateSystem(meeting, 1, "System", new string('x', 400), Guid.NewGuid(), BaseTime);
        var last = MeetingMessage.CreateSystem(meeting, 2, "系统😀", "中文😀\\value\r\nnext", Guid.NewGuid(), BaseTime);
        var initial = await new AgentContextBuilder(new MessageRepositoryFake([older, last])).BuildInitialAsync(meeting, 2, 256);
        var window = new AgentContextWindow(initial.SnapshotSequence, initial.CandidateFirstSequence, initial.CandidateLastSequence,
            initial.DeliveredFirstSequence, initial.DeliveredLastSequence, initial.DeclaredCharacterLimit,
            initial.DeliveredCharacterCount, initial.Truncated, initial.OverLimit, initial.TruncatedMessageCount, initial.ContextFormatVersion);
        var later = MeetingMessage.CreateSystem(meeting, 3, "System", "later", Guid.NewGuid(), BaseTime);
        // Older truncated history is unnecessary for replay; selection is fixed by the saved window.
        var replay = await new AgentContextBuilder(new MessageRepositoryFake([last, later])).RebuildFrozenAsync(meeting, window);
        Assert.Equal(initial with { Messages = replay.Messages }, replay);
        Assert.Equal(last.ID, Assert.Single(replay.Messages).MessageId);
        await Assert.ThrowsAsync<ArgumentException>(() => new AgentContextBuilder(new MessageRepositoryFake([later])).RebuildFrozenAsync(meeting, window));
        var unsupported = new AgentContextWindow(0, null, null, null, null, 256, 0, false, false, 0, "future-version");
        await Assert.ThrowsAsync<ArgumentException>(() => new AgentContextBuilder(new MessageRepositoryFake([])).RebuildFrozenAsync(meeting, unsupported));
    }

    [Fact]
    public async Task TaskContextRejectsMissingDuplicateAndCrossMeetingMessages()
    {
        var meeting = Guid.NewGuid();
        var first = MeetingMessage.CreateSystem(meeting, 1, "System", "first", Guid.NewGuid(), BaseTime);
        var third = MeetingMessage.CreateSystem(meeting, 3, "System", "third", Guid.NewGuid(), BaseTime);
        var foreign = MeetingMessage.CreateSystem(Guid.NewGuid(), 1, "System", "foreign", Guid.NewGuid(), BaseTime);
        foreach (var candidates in new[] { new[] { first, third }, new[] { first, first }, new[] { foreign }, Array.Empty<MeetingMessage>() })
        {
            var builder = new AgentContextBuilder(new MessageRepositoryFake(candidates));
            await Assert.ThrowsAsync<ArgumentException>(() => builder.BuildTaskAsync(meeting, 0, 3, 12000));
        }
    }

    [Fact]
    public async Task OversizedTriggerIsCompleteAndOlderMessagesAreTrimmedContinuously()
    {
        var meeting = Guid.NewGuid();
        var older = MeetingMessage.CreateSystem(meeting, 1, "System", "older", Guid.NewGuid(), BaseTime);
        var trigger = MeetingMessage.CreateSystem(meeting, 2, "System", new string('中', 400), Guid.NewGuid(), BaseTime);
        var builder = new AgentContextBuilder(new MessageRepositoryFake([older, trigger]));
        var result = await builder.BuildTaskAsync(meeting, 0, 2, 256);
        Assert.Equal(trigger.Content, Assert.Single(result.Messages).Content);
        Assert.True(result.OverLimit);
        Assert.True(result.Truncated);
        Assert.Equal(1, result.TruncatedMessageCount);
        Assert.Equal(2, result.DeliveredFirstSequence);
        Assert.Equal(2, result.DeliveredLastSequence);
    }

	private sealed class MessageRepositoryFake(IReadOnlyList<MeetingMessage> messages) : IMessageRepository
	{
        public Task<IReadOnlyList<MessageMention>> ListMentionsForMessagesAsync(Guid meetingId, IReadOnlyList<Guid> messageIds, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<MessageMention>>([]);

		public Task<MessageSequenceSummary> GetSequenceSummaryAsync(Guid meetingId, CancellationToken cancellationToken = default(CancellationToken))
		{
			MeetingMessage[] array = messages.Where((MeetingMessage message) => message.MeetingId == meetingId).ToArray();
			return Task.FromResult((array.Length == 0) ? new MessageSequenceSummary(0L, null, 0) : new MessageSequenceSummary(array.Max((MeetingMessage message) => message.Sequence), array.Min((MeetingMessage message) => message.Sequence), array.Length));
		}

		public Task<IReadOnlyList<MeetingMessage>> ListThroughSequenceAsync(Guid meetingId, long throughSequence, CancellationToken cancellationToken = default(CancellationToken))
		{
			return Task.FromResult((IReadOnlyList<MeetingMessage>)(from message in messages
				where message.MeetingId == meetingId && message.Sequence <= throughSequence
				orderby message.Sequence
				select message).ToArray());
		}

		public Task<MeetingMessage?> FindBySubmissionAsync(Guid meetingId, Guid participantId, Guid submissionId, CancellationToken cancellationToken = default(CancellationToken))
		{
			return Task.FromResult<MeetingMessage?>(null);
		}

		public Task<MeetingMessage?> FindSystemBySubmissionAsync(Guid meetingId, Guid submissionId, CancellationToken cancellationToken = default(CancellationToken))
		{
			return Task.FromResult<MeetingMessage?>(null);
		}

		public Task<long> GetNextSequenceAsync(Guid meetingId, CancellationToken cancellationToken = default(CancellationToken))
		{
			return Task.FromResult(1L);
		}

		public Task<IReadOnlyList<MeetingMessage>> ListAsync(Guid meetingId, long? before, long? after, long? through, int limit, CancellationToken cancellationToken = default(CancellationToken))
		{
			return Task.FromResult((IReadOnlyList<MeetingMessage>)Array.Empty<MeetingMessage>());
		}

		public Task<bool> HasBeforeAsync(Guid meetingId, long sequence, CancellationToken cancellationToken = default(CancellationToken))
		{
			return Task.FromResult(result: false);
		}

		public Task<bool> HasAfterAsync(Guid meetingId, long sequence, long? through, CancellationToken cancellationToken = default(CancellationToken))
		{
			return Task.FromResult(result: false);
		}

		public Task<MeetingMessage?> FindByIdAsync(Guid meetingId, Guid id, CancellationToken cancellationToken = default(CancellationToken))
		{
			return Task.FromResult(messages.SingleOrDefault((MeetingMessage message) => message.MeetingId == meetingId && message.ID == id));
		}

		public Task<IReadOnlyList<MeetingMessage>> ListAfterSequenceThroughAsync(Guid meetingId, long afterSequence, long throughSequence, CancellationToken cancellationToken = default(CancellationToken))
		{
			return Task.FromResult((IReadOnlyList<MeetingMessage>)(from message in messages
				where message.MeetingId == meetingId && message.Sequence > afterSequence && message.Sequence <= throughSequence
				orderby message.Sequence
				select message).ToArray());
		}

		public Task<IReadOnlyList<MessageMention>> ListMentionsAsync(Guid meetingId, Guid messageId, CancellationToken cancellationToken = default(CancellationToken))
		{
			return Task.FromResult((IReadOnlyList<MessageMention>)messages.Where((MeetingMessage message) => message.MeetingId == meetingId && message.ID == messageId).SelectMany((MeetingMessage _) => Array.Empty<MessageMention>()).ToArray());
		}
	}

	private static readonly DateTimeOffset BaseTime = DateTimeOffset.Parse("2026-09-04T00:00:00Z");

	[Fact]
	public async Task EmptyInitialContextKeepsTheSnapshotBoundary()
	{
		AgentContextDto result = await new AgentContextBuilder(new MessageRepositoryFake(Array.Empty<MeetingMessage>())).BuildInitialAsync(Guid.NewGuid(), 0L, 256);
		Assert.Equal("agent-context-v1", result.ContextFormatVersion);
		Assert.Equal(0L, result.SnapshotSequence);
		Assert.Empty(result.Messages);
		Assert.Null(result.CandidateFirstSequence);
		Assert.False(result.Truncated);
		Assert.False(result.OverLimit);
	}

	[Fact]
	public async Task InitialContextKeepsTheMostRecentContinuousSuffix()
	{
		Guid meetingId = Guid.NewGuid();
		Guid participantId = Guid.NewGuid();
		MeetingMessage[] messages = new MeetingMessage[3]
		{
			MeetingMessage.CreateParticipant(meetingId, 1L, participantId, MessageAuthorKind.Human, "Alice", "first", Guid.NewGuid(), BaseTime),
			MeetingMessage.CreateSystem(meetingId, 2L, "System", "second", Guid.NewGuid(), BaseTime.AddSeconds(1.0)),
			MeetingMessage.CreateParticipant(meetingId, 3L, participantId, MessageAuthorKind.Agent, "Reviewer", "third", Guid.NewGuid(), BaseTime.AddSeconds(2.0))
		};
		MessageRepositoryFake repository = new MessageRepositoryFake(messages);
		AgentContextBuilder builder = new AgentContextBuilder(repository);
		AgentContextDto lastOnly = await builder.BuildInitialAsync(meetingId, 3L, BlockLength(messages[2]));
		Assert.Equal(new[] { 3L }, lastOnly.Messages.Select(message => message.Sequence));
		Assert.Equal(1L, lastOnly.CandidateFirstSequence);
		Assert.Equal(3L, lastOnly.CandidateLastSequence);
		Assert.Equal(3L, lastOnly.DeliveredFirstSequence);
		Assert.Equal(3L, lastOnly.DeliveredLastSequence);
		Assert.True(lastOnly.Truncated);
		Assert.Equal(2, lastOnly.TruncatedMessageCount);
		Assert.False(lastOnly.OverLimit);
	}

	[Fact]
	public async Task StableBlockCountsUtf16AndEscapesBackslashAndNewlines()
	{
		Guid meetingId = Guid.NewGuid();
		MeetingMessage message = MeetingMessage.CreateSystem(meetingId, 1L, "Sys\\tem", "line 1\r\nline 2 \ud83d\ude00", Guid.NewGuid(), BaseTime);
		AgentContextDto result = await new AgentContextBuilder(new MessageRepositoryFake(new[] { message })).BuildInitialAsync(meetingId, 1L, 1);
		Assert.True(result.OverLimit);
		Assert.False(result.Truncated);
		Assert.Equal(BlockLength(message), result.DeliveredCharacterCount);
		Assert.Equal(message.Content, Assert.Single(result.Messages).Content);
	}

	[Fact]
	public void DefaultOptionsSatisfyTheStartupOrderingRules()
	{
		AgentCollaborationOptions agentCollaborationOptions = new AgentCollaborationOptions();
		Assert.True(agentCollaborationOptions.MinimumContextCharacterLimit <= agentCollaborationOptions.DefaultContextCharacterLimit);
		Assert.True(agentCollaborationOptions.DefaultContextCharacterLimit <= agentCollaborationOptions.MaximumContextCharacterLimit);
		Assert.All(new[] { agentCollaborationOptions.WaitTimeoutSeconds, agentCollaborationOptions.ReplyCharacterLimit, agentCollaborationOptions.JoinRateLimitWindowSeconds, agentCollaborationOptions.JoinRateLimitPerSource, agentCollaborationOptions.JoinRateLimitPerMeetingCode, agentCollaborationOptions.JoinRateLimitPerName, agentCollaborationOptions.JoinRateLimitPerConnection, agentCollaborationOptions.WakeSignalCapacity }, delegate(int value)
		{
			Assert.True(value > 0);
		});
	}

	private static int BlockLength(MeetingMessage message)
	{
		return $"messageId={message.ID:D}\n".Length + $"sequence={message.Sequence}\n".Length + ("authorKind=" + message.AuthorKind.ToString().ToLowerInvariant() + "\n").Length + ("authorDisplayName=" + Escape(message.AuthorDisplayName) + "\n").Length + ("content=" + Escape(message.Content) + "\n").Length;
	}

	private static string Escape(string value)
	{
		return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
	}
}
