using System.Collections.Generic;
using System;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
namespace RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;


public static class AgentCollaborationContract
{
	public const string ProtocolVersion = "agent-messaging-v1";

	public const string JoinToolName = "join_meeting";

	public const string WaitToolName = "wait_for_mention";

	public const string ReplyToolName = "reply_to_mention";

	public const string StatusToolName = "get_agent_status";
}

public sealed partial record AgentConnectionDto(Guid ConnectionSessionId, string State, int DeclaredContextCharacterLimit, string InitialContextState);

public sealed record AgentContextDto(string ContextFormatVersion, long SnapshotSequence, long? CandidateFirstSequence, long? CandidateLastSequence, long? DeliveredFirstSequence, long? DeliveredLastSequence, int DeclaredCharacterLimit, int DeliveredCharacterCount, bool Truncated, bool OverLimit, int TruncatedMessageCount, IReadOnlyList<AgentContextMessageDto> Messages);

public sealed record AgentContextMentionDto(Guid TargetParticipantId, string TargetDisplayName, int StartOffset, int Length);

public sealed record AgentContextMessageDto(Guid MessageId, Guid MeetingId, long Sequence, string AuthorKind, Guid? AuthorParticipantId, string AuthorDisplayName, string Content, IReadOnlyList<AgentContextMentionDto> Mentions, DateTimeOffset CreatedAtUtc);

public sealed partial record AgentJoinRequest(string? MeetingCode, string? DisplayName, int ContextCharacterLimit, string? SourceAddress = null);

public sealed partial record AgentJoinResultDto(AgentMeetingDto Meeting, AgentParticipantDto Agent, AgentConnectionDto Connection, IReadOnlyList<AgentParticipantDto> Participants, string ResidentInstructionVersion, string ResidentInstruction, AgentContextDto InitialContext, string NextAction);

public sealed record AgentMeetingDto(Guid MeetingId, string Name, string? Description);

public sealed partial record AgentOperationResult(string ProtocolVersion, bool Succeeded, string? ErrorCode, string Message, object? Data)
{
	public static AgentOperationResult Success(object data, string message)
	{
		return new AgentOperationResult("agent-messaging-v1", Succeeded: true, null, message, data);
	}

	public static AgentOperationResult Failure(string code, string message)
	{
		return new AgentOperationResult("agent-messaging-v1", Succeeded: false, code, message, null);
	}
}

public sealed record AgentParticipantDto(Guid ParticipantId, string DisplayName, string ParticipantType);

public sealed record AgentReplyRequest(Guid? ConnectionSessionId, Guid? TaskId, Guid? DeliveryAttemptId, Guid? ReplyIdempotencyKey, string? Content);

public sealed partial record AgentStatusRequest(Guid? ConnectionSessionId);

public sealed partial record AgentWaitRequest(Guid? ConnectionSessionId);

public sealed partial record AgentMentionResultDto(
	string Kind,
	Guid TaskId,
	string TaskState,
	Guid TriggerMessageId,
	long TriggerSequence,
	Guid TriggerMentionId,
	Guid? SourceParticipantId,
	Guid DeliveryAttemptId,
	Guid ReplyIdempotencyKey,
	AgentContextDto Context,
	string NextAction);

public sealed record AgentWaitIdleDto(string Kind, string NextAction);

public sealed partial record AgentReplyResultDto(MessageDto Message, bool Replayed, string NextAction);

public sealed partial record AgentTaskSummaryDto(Guid TaskId, long TriggerSequence, string State, string? FailureCode);

public sealed partial record AgentStatusResultDto(
	Guid MeetingId,
	Guid ParticipantId,
	AgentConnectionDto Connection,
	IReadOnlyDictionary<string, int> TaskCounts,
	AgentTaskSummaryDto? QueueHead,
	long DeliveredThroughSequence,
	long AllocatedThroughSequence);
