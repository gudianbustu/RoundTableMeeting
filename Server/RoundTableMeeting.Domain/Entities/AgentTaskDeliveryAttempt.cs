using System;
using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.Domain.Entities;

public sealed partial class AgentTaskDeliveryAttempt : BaseEntity
{
	private const long MaximumSequence = 9007199254740991L;

	public Guid TaskId { get; private set; }

	public Guid ConnectionSessionId { get; private set; }

	public Guid MeetingId { get; private set; }

	public long CandidateFirstSequence { get; private set; }

	public long CandidateLastSequence { get; private set; }

	public long? DeliveredFirstSequence { get; private set; }

	public long? DeliveredLastSequence { get; private set; }

	public int DeclaredContextCharacterLimit { get; private set; }

	public int DeliveredCharacterCount { get; private set; }

	public bool Truncated { get; private set; }

	public bool OverLimit { get; private set; }

	public int TruncatedMessageCount { get; private set; }

	public AgentTaskDeliveryAttemptState State { get; private set; }

	public DateTimeOffset CreatedAtUtc { get; private set; }

	public DateTimeOffset? DeliveredAtUtc { get; private set; }

	public AgentTaskDeliveryAttempt()
	{
	}

	private AgentTaskDeliveryAttempt(Guid taskId, Guid connectionSessionId, Guid meetingId, long candidateFirstSequence, long candidateLastSequence, int declaredContextCharacterLimit, int deliveredCharacterCount, bool truncated, bool overLimit, int truncatedMessageCount, DateTimeOffset createdAtUtc)
	{
		if (taskId == Guid.Empty)
		{
			throw new ArgumentException("Task is required.", "taskId");
		}
		if (connectionSessionId == Guid.Empty)
		{
			throw new ArgumentException("Connection session is required.", "connectionSessionId");
		}
		if (meetingId == Guid.Empty)
		{
			throw new ArgumentException("Meeting is required.", "meetingId");
		}
		if (candidateFirstSequence < 1 || candidateFirstSequence > candidateLastSequence || candidateLastSequence > 9007199254740991L)
		{
			throw new ArgumentOutOfRangeException("candidateFirstSequence");
		}
		if (declaredContextCharacterLimit < 1)
		{
			throw new ArgumentOutOfRangeException("declaredContextCharacterLimit");
		}
		if (deliveredCharacterCount < 0)
		{
			throw new ArgumentOutOfRangeException("deliveredCharacterCount");
		}
		if (truncatedMessageCount < 0)
		{
			throw new ArgumentOutOfRangeException("truncatedMessageCount");
		}
		if (truncated != truncatedMessageCount > 0)
		{
			throw new ArgumentException("Truncation metadata is inconsistent.", "truncated");
		}
		if (overLimit != deliveredCharacterCount > declaredContextCharacterLimit)
		{
			throw new ArgumentException("Over-limit metadata is inconsistent.", "overLimit");
		}
		TaskId = taskId;
		ConnectionSessionId = connectionSessionId;
		MeetingId = meetingId;
		CandidateFirstSequence = candidateFirstSequence;
		CandidateLastSequence = candidateLastSequence;
		DeclaredContextCharacterLimit = declaredContextCharacterLimit;
		DeliveredCharacterCount = deliveredCharacterCount;
		Truncated = truncated;
		OverLimit = overLimit;
		TruncatedMessageCount = truncatedMessageCount;
		State = AgentTaskDeliveryAttemptState.Prepared;
		CreatedAtUtc = createdAtUtc;
	}

	public static AgentTaskDeliveryAttempt Create(Guid taskId, Guid connectionSessionId, Guid meetingId, long candidateFirstSequence, long candidateLastSequence, int declaredContextCharacterLimit, int deliveredCharacterCount, bool truncated, bool overLimit, int truncatedMessageCount, DateTimeOffset createdAtUtc)
	{
		return new AgentTaskDeliveryAttempt(taskId, connectionSessionId, meetingId, candidateFirstSequence, candidateLastSequence, declaredContextCharacterLimit, deliveredCharacterCount, truncated, overLimit, truncatedMessageCount, createdAtUtc);
	}

	public void MarkDelivered(long deliveredFirstSequence, long deliveredLastSequence, DateTimeOffset deliveredAtUtc)
	{
		if (State != AgentTaskDeliveryAttemptState.Prepared)
		{
			throw new InvalidOperationException("The delivery attempt is not prepared.");
		}
		if (deliveredFirstSequence < CandidateFirstSequence || deliveredFirstSequence > deliveredLastSequence || deliveredLastSequence > CandidateLastSequence)
		{
			throw new ArgumentOutOfRangeException("deliveredFirstSequence");
		}
		if (deliveredAtUtc < CreatedAtUtc)
		{
			throw new ArgumentOutOfRangeException("deliveredAtUtc");
		}
		DeliveredFirstSequence = deliveredFirstSequence;
		DeliveredLastSequence = deliveredLastSequence;
		DeliveredAtUtc = deliveredAtUtc;
		State = AgentTaskDeliveryAttemptState.Delivered;
	}
}
