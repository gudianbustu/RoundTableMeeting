using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
namespace RoundTableMeeting.Abstraction.Services;
public interface IMessageAppendService
{
    Task<MessageOperationResult<MessageDto>> AppendHumanAsync(Guid meetingId, SubmitMessageRequest request, CancellationToken cancellationToken = default);
    Task<MessageOperationResult<MessageDto>> AppendSystemAsync(Guid meetingId, SystemMessageSubmission submission, CancellationToken cancellationToken = default);
}
public interface IMessageHistoryService { Task<MessageOperationResult<MessagePageDto>> GetPageAsync(Guid meetingId, MessagePageRequest request, CancellationToken cancellationToken = default); }
public interface IMeetingChatSubscriptionService { Task<MessageOperationResult<MeetingChatSubscriptionAccessDto>> AuthorizeAsync(Guid meetingId, Guid userId, CancellationToken cancellationToken = default); }
public interface IMessageCleanupService { Task DeleteForMeetingAsync(Guid meetingId, CancellationToken cancellationToken = default); }
public interface IMessageEventRecoveryService { Task<bool> ResumeAsync(Guid deliveryId, CancellationToken cancellationToken = default); }
public interface IMeetingRealtimeTermination { Task NotifyAndTerminateAsync(Guid meetingId, DateTimeOffset deletedAtUtc, CancellationToken cancellationToken = default); }
public interface IMeetingRealtimeGate
{
    ValueTask<IMeetingRealtimeSharedLease?> AcquireSharedAsync(Guid meetingId, CancellationToken cancellationToken = default);
    ValueTask<IMeetingRealtimeExclusiveLease?> AcquireExclusiveCloseAsync(Guid meetingId, TimeSpan timeout, CancellationToken cancellationToken = default);
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
