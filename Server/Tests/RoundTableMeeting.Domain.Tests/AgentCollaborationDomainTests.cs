using System;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;
using Xunit;

namespace RoundTableMeeting.Domain.Tests;

public sealed class AgentCollaborationDomainTests
{
	private static readonly DateTimeOffset BaseTime = DateTimeOffset.Parse("2026-09-04T00:00:00Z");

	[Fact]
	public void ConnectionSessionConfirmsOnceAndDisconnectsIdempotently()
	{
		AgentConnectionSession session = AgentConnectionSession.Create(Guid.NewGuid(), Guid.NewGuid(), 12000, 8L, "binding-1", BaseTime);
		Assert.Equal(AgentConnectionState.Connected, session.State);
		Assert.Equal(InitialContextState.Prepared, session.InitialContextState);
		Assert.True(session.ConfirmInitialContext(BaseTime.AddSeconds(1.0)));
		Assert.False(session.ConfirmInitialContext(BaseTime.AddSeconds(2.0)));
		Assert.Equal(BaseTime.AddSeconds(1.0), session.InitialContextConfirmedAtUtc);
		Assert.True(session.Disconnect(BaseTime.AddSeconds(3.0)));
		Assert.False(session.Disconnect(BaseTime.AddSeconds(4.0)));
		Assert.Equal(AgentConnectionState.Disconnected, session.State);
		Assert.Throws<InvalidOperationException>(delegate
		{
			session.Touch(BaseTime.AddSeconds(5.0));
		});
	}

	[Fact]
	public void ConnectionSessionRejectsInvalidIdentityLimitsAndTimeRegression()
	{
		Assert.Throws<ArgumentException>(() => AgentConnectionSession.Create(Guid.Empty, Guid.NewGuid(), 1, 0L, "binding", BaseTime));
		Assert.Throws<ArgumentException>(() => AgentConnectionSession.Create(Guid.NewGuid(), Guid.Empty, 1, 0L, "binding", BaseTime));
		Assert.Throws<ArgumentOutOfRangeException>(() => AgentConnectionSession.Create(Guid.NewGuid(), Guid.NewGuid(), 0, 0L, "binding", BaseTime));
		Assert.Throws<ArgumentOutOfRangeException>(() => AgentConnectionSession.Create(Guid.NewGuid(), Guid.NewGuid(), 1, -1L, "binding", BaseTime));
		Assert.Throws<ArgumentOutOfRangeException>(() => AgentConnectionSession.Create(Guid.NewGuid(), Guid.NewGuid(), 1, 9007199254740992L, "binding", BaseTime));
		Assert.Throws<ArgumentException>(() => AgentConnectionSession.Create(Guid.NewGuid(), Guid.NewGuid(), 1, 0L, " ", BaseTime));
		AgentConnectionSession session = AgentConnectionSession.Create(Guid.NewGuid(), Guid.NewGuid(), 1, 0L, "binding", BaseTime);
		Assert.Throws<ArgumentOutOfRangeException>(delegate
		{
			session.Touch(BaseTime.AddSeconds(-1.0));
		});
		Assert.Throws<ArgumentOutOfRangeException>(() => session.Disconnect(BaseTime.AddSeconds(-1.0)));
	}

	[Fact]
	public void PreparedDisconnectedSessionCannotBeConfirmed()
	{
		AgentConnectionSession session = AgentConnectionSession.Create(Guid.NewGuid(), Guid.NewGuid(), 1, 0L, "binding", BaseTime);
		session.Disconnect(BaseTime.AddSeconds(1.0));
		Assert.Throws<InvalidOperationException>(() => session.ConfirmInitialContext(BaseTime.AddSeconds(2.0)));
	}

	[Fact]
	public void ContextCursorMaintainsMonotonicAllocatedAndDeliveredPositions()
	{
		AgentContextCursor cursor = AgentContextCursor.Create(Guid.NewGuid(), Guid.NewGuid(), 5L, BaseTime);
		Assert.Equal(0L, cursor.DeliveredThroughSequence);
		Assert.Equal(5L, cursor.AllocatedThroughSequence);
		Assert.True(cursor.AdvanceAllocatedThrough(10L, BaseTime.AddSeconds(1.0)));
		Assert.True(cursor.AdvanceDeliveredThrough(7L, BaseTime.AddSeconds(2.0)));
		Assert.False(cursor.AdvanceDeliveredThrough(7L, BaseTime.AddSeconds(3.0)));
		Assert.False(cursor.AdvanceAllocatedThrough(10L, BaseTime.AddSeconds(3.0)));
		Assert.Throws<InvalidOperationException>(() => cursor.AdvanceAllocatedThrough(9L, BaseTime.AddSeconds(4.0)));
		Assert.Throws<InvalidOperationException>(() => cursor.AdvanceDeliveredThrough(11L, BaseTime.AddSeconds(4.0)));
		Assert.Throws<InvalidOperationException>(() => cursor.AdvanceDeliveredThrough(6L, BaseTime.AddSeconds(4.0)));
		Assert.Throws<ArgumentOutOfRangeException>(() => cursor.AdvanceDeliveredThrough(8L, BaseTime.AddSeconds(1.0)));
		Assert.Throws<ArgumentOutOfRangeException>(() => cursor.AdvanceAllocatedThrough(9007199254740992L, BaseTime.AddSeconds(4.0)));
	}

	[Fact]
	public void ReplyTaskUsesTriggerAsCandidateEndAndFollowsNormalStateMachine()
	{
		AgentReplyTask task = CreateTask();
		Assert.Equal(AgentReplyTaskState.Queued, task.State);
		Assert.Equal(task.TriggerSequence, task.CandidateToSequenceInclusive);
		task.MarkDelivered(BaseTime.AddSeconds(1.0));
		task.MarkProcessing(BaseTime.AddSeconds(2.0));
		task.MarkReplied(Guid.NewGuid(), BaseTime.AddSeconds(3.0));
		Assert.Equal(AgentReplyTaskState.Replied, task.State);
		Assert.NotNull(task.ReplyMessageId);
		Assert.NotNull(task.RepliedAtUtc);
		Assert.Throws<InvalidOperationException>(delegate
		{
			task.MarkProcessing(BaseTime.AddSeconds(4.0));
		});
		Assert.False(task.Cancel(BaseTime.AddSeconds(4.0)));
	}

	[Fact]
	public void ReplyTaskValidatesSourceRangeAndTerminalTransitions()
	{
		Assert.Throws<ArgumentException>(() => AgentReplyTask.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1L, Guid.NewGuid(), MessageAuthorKind.Human, null, 0L, Guid.NewGuid(), BaseTime));
		Assert.Throws<ArgumentException>(() => AgentReplyTask.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1L, Guid.NewGuid(), MessageAuthorKind.System, Guid.NewGuid(), 0L, Guid.NewGuid(), BaseTime));
		Assert.Throws<ArgumentOutOfRangeException>(() => AgentReplyTask.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1L, Guid.NewGuid(), MessageAuthorKind.Human, Guid.NewGuid(), 1L, Guid.NewGuid(), BaseTime));
		Assert.Throws<ArgumentOutOfRangeException>(() => AgentReplyTask.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1L, Guid.NewGuid(), (MessageAuthorKind)999, null, 0L, Guid.NewGuid(), BaseTime));
		AgentReplyTask failed = CreateTask();
		failed.MarkFailed("context_invalid", BaseTime.AddSeconds(1.0));
		Assert.Equal(AgentReplyTaskState.Failed, failed.State);
		Assert.Equal("context_invalid", failed.FailureCode);
		Assert.Throws<InvalidOperationException>(delegate
		{
			failed.MarkFailed("again", BaseTime.AddSeconds(2.0));
		});
		Assert.True(failed.Cancel(BaseTime.AddSeconds(2.0)));
		Assert.Equal(AgentReplyTaskState.Cancelled, failed.State);
		AgentReplyTask failedLate = CreateTask();
		failedLate.MarkFailed("context_invalid", BaseTime.AddSeconds(3.0));
		Assert.Throws<ArgumentOutOfRangeException>(() => failedLate.Cancel(BaseTime.AddSeconds(2.0)));
		AgentReplyTask agentReplyTask = CreateTask();
		Assert.True(agentReplyTask.Cancel(BaseTime.AddSeconds(1.0)));
		Assert.False(agentReplyTask.Cancel(BaseTime.AddSeconds(2.0)));
		Assert.Equal(AgentReplyTaskState.Cancelled, agentReplyTask.State);
	}

	[Fact]
	public void ReplyTaskRejectsInvalidReplyAndStateTimes()
	{
		AgentReplyTask task = CreateTask();
		Assert.Throws<ArgumentOutOfRangeException>(delegate
		{
			task.MarkDelivered(BaseTime.AddSeconds(-1.0));
		});
		task.MarkDelivered(BaseTime.AddSeconds(1.0));
		Assert.Throws<ArgumentOutOfRangeException>(delegate
		{
			task.MarkProcessing(BaseTime);
		});
		task.MarkProcessing(BaseTime.AddSeconds(2.0));
		Assert.Throws<ArgumentException>(delegate
		{
			task.MarkReplied(Guid.Empty, BaseTime.AddSeconds(3.0));
		});
		Assert.Throws<ArgumentOutOfRangeException>(delegate
		{
			task.MarkReplied(Guid.NewGuid(), BaseTime.AddSeconds(1.0));
		});
	}

	[Fact]
	public void DeliveryAttemptsHaveIndependentIdsAndBoundedDeliveredRange()
	{
		Guid guid = Guid.NewGuid();
		Guid connectionSessionId = Guid.NewGuid();
		AgentTaskDeliveryAttempt first = AgentTaskDeliveryAttempt.Create(guid, connectionSessionId, Guid.NewGuid(), 1L, 10L, 12000, 40, truncated: true, overLimit: false, 2, BaseTime);
		AgentTaskDeliveryAttempt agentTaskDeliveryAttempt = AgentTaskDeliveryAttempt.Create(guid, connectionSessionId, first.MeetingId, 1L, 10L, 12000, 40, truncated: true, overLimit: false, 2, BaseTime);
		Assert.NotEqual(first.ID, agentTaskDeliveryAttempt.ID);
		Assert.Equal(guid, first.TaskId);
		Assert.Equal(AgentTaskDeliveryAttemptState.Prepared, first.State);
		first.MarkDelivered(3L, 8L, BaseTime.AddSeconds(1.0));
		Assert.Equal(AgentTaskDeliveryAttemptState.Delivered, first.State);
		Assert.Equal(3L, first.DeliveredFirstSequence);
		Assert.Equal(8L, first.DeliveredLastSequence);
		Assert.Throws<InvalidOperationException>(delegate
		{
			first.MarkDelivered(4L, 9L, BaseTime.AddSeconds(2.0));
		});
		AgentTaskDeliveryAttempt invalid = AgentTaskDeliveryAttempt.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1L, 2L, 1, 0, truncated: false, overLimit: false, 0, BaseTime);
		Assert.Throws<ArgumentOutOfRangeException>(delegate
		{
			invalid.MarkDelivered(0L, 1L, BaseTime.AddSeconds(1.0));
		});
		Assert.Throws<ArgumentOutOfRangeException>(delegate
		{
			invalid.MarkDelivered(1L, 3L, BaseTime.AddSeconds(1.0));
		});
		Assert.Throws<ArgumentOutOfRangeException>(delegate
		{
			invalid.MarkDelivered(2L, 1L, BaseTime.AddSeconds(1.0));
		});
		Assert.Throws<ArgumentException>(() => AgentTaskDeliveryAttempt.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1L, 2L, 10, 5, truncated: true, overLimit: false, 0, BaseTime));
		Assert.Throws<ArgumentException>(() => AgentTaskDeliveryAttempt.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1L, 2L, 10, 11, truncated: false, overLimit: false, 0, BaseTime));
	}

	private static AgentReplyTask CreateTask()
	{
		return AgentReplyTask.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 4L, Guid.NewGuid(), MessageAuthorKind.Human, Guid.NewGuid(), 2L, Guid.NewGuid(), BaseTime);
	}
}
