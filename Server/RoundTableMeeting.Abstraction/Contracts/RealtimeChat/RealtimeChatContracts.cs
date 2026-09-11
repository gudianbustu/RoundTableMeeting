namespace RoundTableMeeting.Abstraction.Contracts.RealtimeChat;

public sealed record MentionCandidateDto(Guid ParticipantId, int StartOffset, int Length);
public sealed record SubmitMessageRequest(Guid ClientSubmissionId, string Content, IReadOnlyList<MentionCandidateDto>? MentionCandidates = null);
public sealed record MessageAuthorDto(Guid? ParticipantId, MessageAuthorKind Kind, string DisplayName);
public sealed record MessageMentionDto(Guid ParticipantId, string DisplayName, int StartOffset, int Length);
public sealed record MessageDto(Guid MessageId, Guid MeetingId, long Sequence, MessageAuthorDto Author, string Content, IReadOnlyList<MessageMentionDto> Mentions, DateTimeOffset CreatedAt);
public sealed record SubmitMessageResultDto(MessageDto Message, bool Replayed);
public sealed record MessagePageRequest(long? BeforeSequence = null, long? AfterSequence = null, long? ThroughSequence = null, int? Limit = null);
public sealed record MessagePageDto(IReadOnlyList<MessageDto> Items, bool HasMoreBefore, bool HasMoreAfter, long? FirstSequence, long? LastSequence, long? NextBeforeSequence, long? NextAfterSequence);
public sealed record MeetingChatSubscriptionAccessDto(Guid ParticipantId, long HighWatermarkSequence);
public sealed record AgentActivityDto(Guid MeetingId, Guid ParticipantId, string State, Guid? ActiveTaskId, string? FailureCode, DateTimeOffset ChangedAt)
{
    public long Revision { get; init; }
    public string? Reason { get; init; }
    public bool? RetryExhausted { get; init; }
    public DateTimeOffset? NextRetryAt { get; init; }
    public string? ConnectionDisposition { get; init; }
}
public sealed record MeetingChatSubscriptionDto(IReadOnlyList<Guid> OnlineParticipantIds, long HighWatermarkSequence, IReadOnlyList<AgentActivityDto> AgentActivities);
public sealed record ParticipantPresenceDto(Guid ParticipantId, string State, DateTimeOffset ChangedAt);
public sealed record MeetingDeletedDto(Guid MeetingId, DateTimeOffset DeletedAt, string ReasonCode = "meeting_deleted");
public sealed record SystemMessageSubmission(string Code, string OccurrenceKey, string AuthorDisplayName, string Content);
public sealed record MessageAppendedEnvelope(Guid EventId, Guid MessageId, Guid MeetingId, long Sequence, DateTimeOffset OccurredAtUtc);
