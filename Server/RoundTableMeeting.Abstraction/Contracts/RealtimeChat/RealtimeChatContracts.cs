namespace RoundTableMeeting.Abstraction.Contracts.RealtimeChat;

public sealed record MentionCandidateDto(Guid ParticipantId, int StartOffset, int Length);
public sealed record McpRecipientSelector(string? RequestedName, Guid? CurrentlyResolvedId);
public sealed record SubmitMessageRequest(Guid ClientSubmissionId, string Content, IReadOnlyList<MentionCandidateDto>? MentionCandidates = null, string? Visibility = null, Guid? RecipientParticipantId = null, IReadOnlyList<Guid>? AttachmentIds = null, Guid? ReplyToMessageId = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool StartsPublicTopic { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public Guid? OriginalTextAttachmentId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public Guid? SourceTextDraftId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public Guid? SourcePendingMessageId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public McpRecipientSelector? SourceRecipient { get; init; }
}
public sealed record MessageAuthorDto(Guid? ParticipantId, MessageAuthorKind Kind, string DisplayName);
public sealed record MessageMentionDto(Guid ParticipantId, string DisplayName, int StartOffset, int Length)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsGroup { get; init; }
}
public sealed record MessageDto(Guid MessageId, Guid MeetingId, long Sequence, MessageAuthorDto Author, string Content, IReadOnlyList<MessageMentionDto> Mentions, DateTimeOffset CreatedAt)
{
    public string Visibility { get; init; } = "public";
    public MessageRecipientDto? Recipient { get; init; }
    public IReadOnlyList<AttachmentDto> Attachments { get; init; } = [];
    public Guid? ReplyToMessageId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public Guid? DiscussionTopicId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool DiscussionInterrupted { get; init; }
    public bool OptionalAgentResponse { get; init; }
    public Guid? OriginalTextAttachmentId { get; init; }
    public bool IsForAgent(Guid participantId)
    {
        if (Author.ParticipantId == participantId || Author.Kind == MessageAuthorKind.System) return false;
        if (Visibility == "private") return Recipient?.ParticipantId == participantId;
        if (DiscussionInterrupted) return false;
        return OptionalAgentResponse && Mentions.Count == 0 || Mentions.Any(m => m.ParticipantId == participantId);
    }
    public string OffsetUnit => "utf16";
}
public sealed record SubmitMessageResultDto(MessageDto Message, bool Replayed);
public sealed record UnreadActivityScopeDto(string Scope, Guid? PeerParticipantId, long Count, long LastSequence);
public sealed record UnreadActivityDto(Guid ParticipantId, long ThroughSequence, IReadOnlyList<UnreadActivityScopeDto> Scopes);
public sealed record MessagePageRequest(long? BeforeSequence = null, long? AfterSequence = null, long? ThroughSequence = null, int? Limit = null, string Scope = "all", Guid? PeerParticipantId = null);
public sealed record MessagePageDto(IReadOnlyList<MessageDto> Items, bool HasMoreBefore, bool HasMoreAfter, long? FirstSequence, long? LastSequence, long? NextBeforeSequence, long? NextAfterSequence)
{
    public long VisibleThroughSequence { get; init; }
}
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

public sealed record MessageRecipientDto(Guid ParticipantId, string DisplayName);
public sealed record AttachmentDto(Guid AttachmentId, string Name, long SizeBytes, string MediaType, string State);
public sealed record SubmissionStatusDto(string Status, MessageDto? Message);
public sealed record AttachmentListItemDto(AttachmentDto Attachment, Guid MessageId, long Sequence, MessageAuthorDto Author, DateTimeOffset CreatedAt);
public sealed record AttachmentPageDto(IReadOnlyList<AttachmentListItemDto> Items, long? NextBeforeSequence);

public sealed record CommunicationCapabilitiesDto(string ProtocolVersion, long MaxFileBytes, string SizeUnit, bool PrivateMessages);
