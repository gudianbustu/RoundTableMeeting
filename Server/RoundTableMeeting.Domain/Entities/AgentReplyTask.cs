using System;
using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.Domain.Entities;

public sealed partial class AgentReplyTask : BaseEntity
{
	private const long MaximumSequence = 9007199254740991L;

	public Guid MeetingId { get; private set; }

	public Guid AgentParticipantId { get; private set; }

	public Guid TriggerMessageId { get; private set; }

	public Guid ChainId { get; private set; }

	public Guid? ParentTaskId { get; private set; }

	public long TriggerSequence { get; private set; }

	public Guid TriggerMentionId { get; private set; }

	public MessageAuthorKind SourceAuthorKind { get; private set; }

	public Guid? SourceParticipantId { get; private set; }

	public long CandidateFromSequenceExclusive { get; private set; }

	public long CandidateToSequenceInclusive { get; private set; }

	public AgentReplyTaskState State { get; private set; }

	public Guid ReplyIdempotencyKey { get; private set; }

	public Guid? ReplyMessageId { get; private set; }

	public string? FailureCode { get; private set; }

	public DateTimeOffset CreatedAtUtc { get; private set; }

	public DateTimeOffset? DeliveredAtUtc { get; private set; }

	public DateTimeOffset? ProcessingAtUtc { get; private set; }

	public DateTimeOffset? RepliedAtUtc { get; private set; }

	public DateTimeOffset? FailedAtUtc { get; private set; }

	public DateTimeOffset? CancelledAtUtc { get; private set; }

	public AgentReplyTask()
	{
	}

	private AgentReplyTask(Guid taskId, Guid meetingId, Guid agentParticipantId, Guid triggerMessageId, long triggerSequence, Guid triggerMentionId, MessageAuthorKind sourceAuthorKind, Guid? sourceParticipantId, long candidateFromSequenceExclusive, Guid replyIdempotencyKey, DateTimeOffset createdAtUtc)
	{
		if (taskId == Guid.Empty)
		{
			throw new ArgumentException("Task is required.", "taskId");
		}
		if (meetingId == Guid.Empty)
		{
			throw new ArgumentException("Meeting is required.", "meetingId");
		}
		if (agentParticipantId == Guid.Empty)
		{
			throw new ArgumentException("Agent participant is required.", "agentParticipantId");
		}
		if (triggerMessageId == Guid.Empty)
		{
			throw new ArgumentException("Trigger message is required.", "triggerMessageId");
		}
		if ((triggerSequence < 1 || triggerSequence > 9007199254740991L) ? true : false)
		{
			throw new ArgumentOutOfRangeException("triggerSequence");
		}
		if (triggerMentionId == Guid.Empty)
		{
			throw new ArgumentException("Trigger mention is required.", "triggerMentionId");
		}
		if (candidateFromSequenceExclusive < 0 || candidateFromSequenceExclusive >= triggerSequence)
		{
			throw new ArgumentOutOfRangeException("candidateFromSequenceExclusive");
		}
		if (replyIdempotencyKey == Guid.Empty)
		{
			throw new ArgumentException("Reply idempotency key is required.", "replyIdempotencyKey");
		}
		ValidateSource(sourceAuthorKind, sourceParticipantId);
		base.ID = taskId;
		MeetingId = meetingId;
		AgentParticipantId = agentParticipantId;
		TriggerMessageId = triggerMessageId;
		ChainId = triggerMessageId;
		TriggerSequence = triggerSequence;
		TriggerMentionId = triggerMentionId;
		SourceAuthorKind = sourceAuthorKind;
		SourceParticipantId = sourceParticipantId;
		CandidateFromSequenceExclusive = candidateFromSequenceExclusive;
		CandidateToSequenceInclusive = triggerSequence;
		State = AgentReplyTaskState.Queued;
		ReplyIdempotencyKey = replyIdempotencyKey;
		CreatedAtUtc = createdAtUtc;
	}

	public static AgentReplyTask Create(Guid taskId, Guid meetingId, Guid agentParticipantId, Guid triggerMessageId, long triggerSequence, Guid triggerMentionId, MessageAuthorKind sourceAuthorKind, Guid? sourceParticipantId, long candidateFromSequenceExclusive, Guid replyIdempotencyKey, DateTimeOffset createdAtUtc)
	{
		if (!Enum.IsDefined(sourceAuthorKind)) throw new ArgumentOutOfRangeException(nameof(sourceAuthorKind));
		if (sourceAuthorKind != MessageAuthorKind.Human)
			throw new ArgumentException("Only Human messages create root tasks; Agent messages require a verified parent.", nameof(sourceAuthorKind));
		return new AgentReplyTask(taskId, meetingId, agentParticipantId, triggerMessageId, triggerSequence, triggerMentionId, sourceAuthorKind, sourceParticipantId, candidateFromSequenceExclusive, replyIdempotencyKey, createdAtUtc);
	}

	public void MarkDelivered(DateTimeOffset deliveredAtUtc)
	{
		EnsureState(AgentReplyTaskState.Queued);
		EnsureTime(deliveredAtUtc);
		State = AgentReplyTaskState.Delivered;
		DeliveredAtUtc = deliveredAtUtc;
	}

	public void MarkProcessing(DateTimeOffset processingAtUtc)
	{
		EnsureState(AgentReplyTaskState.Delivered);
		DateTimeOffset? deliveredAtUtc = DeliveredAtUtc;
		if (processingAtUtc < deliveredAtUtc)
		{
			throw new ArgumentOutOfRangeException("processingAtUtc");
		}
		State = AgentReplyTaskState.Processing;
		ProcessingAtUtc = processingAtUtc;
	}

	public void MarkReplied(Guid replyMessageId, DateTimeOffset repliedAtUtc)
	{
		if (replyMessageId == Guid.Empty)
		{
			throw new ArgumentException("Reply message is required.", "replyMessageId");
		}
		EnsureState(AgentReplyTaskState.Processing);
		DateTimeOffset? processingAtUtc = ProcessingAtUtc;
		if (repliedAtUtc < processingAtUtc)
		{
			throw new ArgumentOutOfRangeException("repliedAtUtc");
		}
		State = AgentReplyTaskState.Replied;
		ReplyMessageId = replyMessageId;
		RepliedAtUtc = repliedAtUtc;
	}

	public void MarkFailed(string failureCode, DateTimeOffset failedAtUtc)
	{
		if (string.IsNullOrWhiteSpace(failureCode) || failureCode.Trim().Length > 128)
		{
			throw new ArgumentException("Failure code is invalid.", "failureCode");
		}
		AgentReplyTaskState state = State;
		if (state != AgentReplyTaskState.Queued && state != AgentReplyTaskState.Delivered && state != AgentReplyTaskState.Processing)
		{
			throw new InvalidOperationException("The task cannot fail from its current state.");
		}
		EnsureNotBeforeLatestState(failedAtUtc);
		State = AgentReplyTaskState.Failed;
		FailureCode = failureCode.Trim();
		FailedAtUtc = failedAtUtc;
	}

	public bool Cancel(DateTimeOffset cancelledAtUtc)
	{
		AgentReplyTaskState state = State;
		if ((state == AgentReplyTaskState.Replied || state == AgentReplyTaskState.Cancelled) ? true : false)
		{
			return false;
		}
		EnsureNotBeforeLatestState(cancelledAtUtc);
		State = AgentReplyTaskState.Cancelled;
        NextRetryAtUtc = null;
        RetryExhausted = false;
        StopReason = null;
		CancelledAtUtc = cancelledAtUtc;
		return true;
	}

	private void EnsureState(AgentReplyTaskState expected)
	{
		if (State != expected)
		{
			throw new InvalidOperationException($"The task must be {expected}.");
		}
	}

	private void EnsureTime(DateTimeOffset value)
	{
		if (value < CreatedAtUtc)
		{
			throw new ArgumentOutOfRangeException("value");
		}
	}

	private void EnsureNotBeforeLatestState(DateTimeOffset value)
	{
		DateTimeOffset dateTimeOffset = FailedAtUtc ?? ProcessingAtUtc ?? DeliveredAtUtc ?? CreatedAtUtc;
		if (value < dateTimeOffset)
		{
			throw new ArgumentOutOfRangeException("value");
		}
	}

	private static void ValidateSource(MessageAuthorKind kind, Guid? participantId)
	{
		if (!Enum.IsDefined(kind))
		{
			throw new ArgumentOutOfRangeException("kind");
		}
		if ((uint)kind <= 1u)
		{
			if (!participantId.HasValue || participantId.Value == Guid.Empty)
			{
				throw new ArgumentException("Participant source is required.", "participantId");
			}
		}
		else if (participantId.HasValue)
		{
			throw new ArgumentException("System source cannot have a participant.", "participantId");
		}
	}
}
