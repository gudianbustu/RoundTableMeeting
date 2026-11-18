using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
namespace RoundTableMeeting.AgentCollaboration;

public sealed record McpResult<T>(bool Succeeded, T? Data, string? ErrorCode = null)
{
    public string ProtocolVersion => "participant-sequence-v2";
    public static McpResult<T> Ok(T value) => new(true, value);
    public static McpResult<T> Fail(string error) => new(false, default, error);
}
public sealed record McpParticipant(Guid ParticipantId, string DisplayName, string Type, string MembershipState, string ConnectionState, bool IsCreator = false);
public sealed record McpConnection(Guid ConnectionSessionId, Guid MeetingId, Guid ParticipantId, string DisplayName,
    long ConnectionGeneration, DateTimeOffset? LeaseExpiresAt, string? LifecycleProof);
public sealed record McpMeetingInfo(Guid MeetingId, string Name, string? Description, IReadOnlyList<McpParticipant> Participants);
public sealed record McpReadCursor(long AfterSequence, int ContentOffset = 0);
public sealed record McpMessageFragment(MessageDto Message, int ContentOffset, int TotalContentCharacters, bool Complete);
public sealed record McpMessages(IReadOnlyList<McpMessageFragment> Items, McpReadCursor NextCursor, long VisibleThroughSequence, bool HasMore, string Kind);
public sealed record McpAcknowledgement(string State);
public sealed class McpOperationException(string code) : Exception(code);
