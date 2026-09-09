using System;

namespace RoundTableMeeting.Domain.Entities;

public sealed class AgentContextCursor : BaseEntity
{
	private const long MaximumSequence = 9007199254740991L;

	public Guid MeetingId { get; private set; }

	public Guid ParticipantId { get; private set; }

	public long DeliveredThroughSequence { get; private set; }

	public long AllocatedThroughSequence { get; private set; }

	public DateTimeOffset UpdatedAtUtc { get; private set; }

	public AgentContextCursor()
	{
	}

	private AgentContextCursor(Guid meetingId, Guid participantId, long allocatedThroughSequence, DateTimeOffset updatedAtUtc)
	{
		if (meetingId == Guid.Empty)
		{
			throw new ArgumentException("Meeting is required.", "meetingId");
		}
		if (participantId == Guid.Empty)
		{
			throw new ArgumentException("Participant is required.", "participantId");
		}
		EnsureSequence(allocatedThroughSequence, "allocatedThroughSequence");
		MeetingId = meetingId;
		ParticipantId = participantId;
		DeliveredThroughSequence = 0L;
		AllocatedThroughSequence = allocatedThroughSequence;
		UpdatedAtUtc = updatedAtUtc;
	}

	public static AgentContextCursor Create(Guid meetingId, Guid participantId, long allocatedThroughSequence, DateTimeOffset updatedAtUtc)
	{
		return new AgentContextCursor(meetingId, participantId, allocatedThroughSequence, updatedAtUtc);
	}

	public bool AdvanceAllocatedThrough(long sequence, DateTimeOffset updatedAtUtc)
	{
		EnsureSequence(sequence, "sequence");
		if (sequence < AllocatedThroughSequence)
		{
			throw new InvalidOperationException("The allocated cursor cannot move backwards.");
		}
		if (sequence == AllocatedThroughSequence)
		{
			return false;
		}
		EnsureUpdateTime(updatedAtUtc);
		AllocatedThroughSequence = sequence;
		UpdatedAtUtc = updatedAtUtc;
		return true;
	}

	public bool AdvanceDeliveredThrough(long sequence, DateTimeOffset updatedAtUtc)
	{
		EnsureSequence(sequence, "sequence");
		if (sequence < DeliveredThroughSequence)
		{
			throw new InvalidOperationException("The delivered cursor cannot move backwards.");
		}
		if (sequence > AllocatedThroughSequence)
		{
			throw new InvalidOperationException("The delivered cursor cannot exceed the allocated cursor.");
		}
		if (sequence == DeliveredThroughSequence)
		{
			return false;
		}
		EnsureUpdateTime(updatedAtUtc);
		DeliveredThroughSequence = sequence;
		UpdatedAtUtc = updatedAtUtc;
		return true;
	}

	private void EnsureUpdateTime(DateTimeOffset value)
	{
		if (value < UpdatedAtUtc)
		{
			throw new ArgumentOutOfRangeException("value");
		}
	}

	private static void EnsureSequence(long value, string parameterName)
	{
		if ((value < 0 || value > 9007199254740991L) ? true : false)
		{
			throw new ArgumentOutOfRangeException(parameterName);
		}
	}
}
