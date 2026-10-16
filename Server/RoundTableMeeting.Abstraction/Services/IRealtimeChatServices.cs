using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
namespace RoundTableMeeting.Abstraction.Services;
public interface IMessageAppendService
{
    Task<MessageOperationResult<MessageDto>> AppendHumanAsync(Guid meetingId, SubmitMessageRequest request, CancellationToken cancellationToken = default);
    Task<MessageOperationResult<MessageDto>> AppendSystemAsync(Guid meetingId, SystemMessageSubmission submission, CancellationToken cancellationToken = default);
}

public interface IResolvedMessageAppendCore
{
    Task<ResolvedMessageAppendResult> AppendScopedAsync(Guid meetingId, ResolvedMessageAuthor author, SubmitMessageRequest request, CancellationToken ct = default)
    {
        if (request.Visibility is not (null or "public") || request.RecipientParticipantId.HasValue
            || request.ReplyToMessageId.HasValue || request.AttachmentIds?.Count > 0)
            throw new NotSupportedException("This implementation does not support scoped messages.");
        return AppendAsync(meetingId, author, request.Content, request.ClientSubmissionId, request.MentionCandidates, ct);
    }
    Task<ResolvedMessageAppendResult> AppendAsync(
        Guid meetingId, ResolvedMessageAuthor author, string content, Guid clientSubmissionId,
        IReadOnlyList<MentionCandidateDto>? mentionCandidates = null, CancellationToken cancellationToken = default);

    Task<ResolvedMessageAppendResult> AppendAgentAsync(
        Guid meetingId,
        Guid participantId,
        string authorDisplayName,
        string content,
        Guid clientSubmissionId,
        CancellationToken cancellationToken = default);
}

public sealed record ResolvedMessageAuthor(Guid? ParticipantId, string DisplayName, MessageAuthorKind Kind);

public sealed record ResolvedMessageAppendResult(
    Guid MessageId,
    Guid MeetingId,
    long Sequence,
    Guid? ParticipantId,
    string AuthorDisplayName,
    string Content,
    DateTimeOffset CreatedAtUtc) { public bool Replayed { get; init; } }
public interface IMessageHistoryService { Task<MessageOperationResult<MessagePageDto>> GetPageAsync(Guid meetingId, MessagePageRequest request, CancellationToken cancellationToken = default); }
public interface IMeetingChatSubscriptionService { Task<MessageOperationResult<MeetingChatSubscriptionAccessDto>> AuthorizeAsync(Guid meetingId, Guid userId, CancellationToken cancellationToken = default); }
public interface IAgentActivityService
{
    Task<IReadOnlyList<AgentActivityDto>> ListAsync(Guid meetingId, CancellationToken cancellationToken = default);
    Task<AgentActivityDto?> FindAsync(Guid meetingId, Guid participantId, CancellationToken cancellationToken = default);
}
public interface IAgentActivityNotifier
{
    Task NotifyAsync(AgentActivityDto activity, bool notifyPresence = false, CancellationToken cancellationToken = default);
}
public interface IMessageCleanupService { Task DeleteForMeetingAsync(Guid meetingId, CancellationToken cancellationToken = default); }
public interface IMessageEventRecoveryService { Task<bool> ResumeAsync(Guid deliveryId, CancellationToken cancellationToken = default); }
public interface IMeetingRealtimeTermination
{
    Task NotifyAndTerminateAsync(Guid meetingId, DateTimeOffset deletedAtUtc, CancellationToken cancellationToken = default);
    Task NotifyDeletingAsync(Guid meetingId, CancellationToken cancellationToken = default);
    Task TerminateParticipantAsync(Guid meetingId, Guid participantId, CancellationToken cancellationToken = default);
}
public interface IMeetingRealtimeGate
{
    ValueTask<IMeetingRealtimeSharedLease?> AcquireSharedAsync(Guid meetingId, CancellationToken cancellationToken = default);
    ValueTask<IMeetingRealtimeExclusiveLease?> AcquireExclusiveCloseAsync(Guid meetingId, TimeSpan timeout, CancellationToken cancellationToken = default);
    ValueTask<IMeetingRealtimeExclusiveLease?> AcquireExclusiveMutationAsync(Guid meetingId, TimeSpan timeout, CancellationToken cancellationToken = default);
    void Forget(Guid meetingId);
}
public interface IMeetingRealtimeSharedLease : IDisposable { }
public interface IMeetingRealtimeExclusiveLease : IDisposable { void CommitClosed(); }
public sealed record MessageEventConsumerDescriptor(string ConsumerName, Type ServiceType);
public interface IMessageEventConsumerCatalog
{
    IReadOnlyList<string> ConsumerNames { get; }
    Type GetRequiredServiceType(string consumerName);
}
public interface IMessageEventConsumer
{
    Task ConsumeAsync(MessageAppendedEnvelope envelope, CancellationToken cancellationToken = default);
}

public interface IMessageLookupService
{
    Task<MessageOperationResult<MessageDto>> GetMessageAsync(Guid meetingId, Guid messageId, CancellationToken ct = default);
    Task<MessageOperationResult<SubmissionStatusDto>> GetSubmissionAsync(Guid meetingId, Guid submissionId, CancellationToken ct = default);
}
