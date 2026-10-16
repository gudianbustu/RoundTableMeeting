using System.Security.Cryptography;
using System.Text;
using Materal.Extensions.DependencyInjection;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Domain.UnitOfWork;
using DomainAuthorKind = RoundTableMeeting.Domain.Enums.MessageAuthorKind;
using TransportAuthorKind = RoundTableMeeting.Abstraction.Contracts.RealtimeChat.MessageAuthorKind;
namespace RoundTableMeeting.Application.RealtimeChat;
public sealed class RealtimeChatService(ICurrentUserContext currentUser, IMeetingRepository meetings, IMeetingParticipantRepository participants, IMessageRepository messages, IMeetingTransactionRunner runner, IResolvedMessageAppendCore appendCore, RealtimeChatOptions options, IMeetingRealtimeGate realtimeGate, IMeetingLifecycleService lifecycleService, IMessageVisibilityRepository visibility, VisibleMessageMapper mapper) : IMessageLookupService, IScopedDependency<IMessageLookupService>, IMessageAppendService, IMessageHistoryService, IMeetingChatSubscriptionService, IScopedDependency<IMessageAppendService>, IScopedDependency<IMessageHistoryService>, IScopedDependency<IMeetingChatSubscriptionService>
{
    public Task<MessageOperationResult<MessageDto>> AppendHumanAsync(Guid meetingId, SubmitMessageRequest request, CancellationToken ct = default) => AppendAsync(meetingId, request, DomainAuthorKind.Human, ct);
    public async Task<MessageOperationResult<MessageDto>> AppendSystemAsync(Guid meetingId, SystemMessageSubmission submission, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(submission.Code) || string.IsNullOrWhiteSpace(submission.OccurrenceKey)) return Invalid<MessageDto>("submission");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(submission.Code + "\0" + submission.OccurrenceKey)); var id = new Guid(bytes[..16]);
        return await AppendAsync(meetingId, new SubmitMessageRequest(id, submission.Content), DomainAuthorKind.System, ct, submission.AuthorDisplayName);
    }
    private async Task<MessageOperationResult<MessageDto>> AppendAsync(Guid meetingId, SubmitMessageRequest request, DomainAuthorKind kind, CancellationToken ct, string? systemName = null)
    {
        if (kind != DomainAuthorKind.System && (currentUser.UserId is not Guid userId || userId == Guid.Empty)) return new(MessageOperationStatus.Unauthenticated);
        using var sharedLease = await realtimeGate.AcquireSharedAsync(meetingId, ct);
        if (sharedLease is null) return new(await UnavailableStatus(meetingId, currentUser.UserId.GetValueOrDefault(), ct));
        var effectiveUserId = currentUser.UserId.GetValueOrDefault();
        if (request.ClientSubmissionId == Guid.Empty) return Invalid<MessageDto>("clientSubmissionId");
        var meeting = await meetings.FindActiveByIdAsync(meetingId, ct); if (meeting is null) return new(await UnavailableStatus(meetingId, effectiveUserId, ct));
        var participant = kind == DomainAuthorKind.System ? null : await participants.FindByUserAsync(meetingId, effectiveUserId, ct); if (kind != DomainAuthorKind.System && participant is null) return new(MessageOperationStatus.MeetingNotFound);
        if (participant?.MembershipState == Domain.Enums.MeetingMembershipState.Left) return new(MessageOperationStatus.MembershipLeft);
        try
        {
            var result = await runner.RunAsync(token => appendCore.AppendScopedAsync(meetingId,
                kind == DomainAuthorKind.System ? new(null, systemName ?? "System", TransportAuthorKind.System)
                    : new(participant!.ID, participant.DisplayName, TransportAuthorKind.Human), request, token), ct);
            var persisted = await messages.FindByIdAsync(meetingId, result.MessageId, ct)
                ?? throw new InvalidOperationException("The committed message could not be read.");
            // System append is internal and has no viewer; its position must never be presented as a user's cursor.
            var row = participant is null ? new VisibleMessage(persisted, 0)
                : await visibility.FindAsync(meetingId, participant.ID, result.MessageId, ct: ct)
                    ?? throw new InvalidOperationException("The committed projection could not be read.");
            return new(result.Replayed ? MessageOperationStatus.Replayed : MessageOperationStatus.Created, await mapper.MapAsync(row, ct));
        }
        catch (RoundTableMeeting.Domain.Exceptions.MeetingMessageAccessException exception)
        {
            return exception.Code switch
            {
                "membership_left" => new(MessageOperationStatus.MembershipLeft),
                "submission_mismatch" => new(MessageOperationStatus.SubmissionMismatch),
                "meeting_not_found" => new(MessageOperationStatus.MeetingNotFound),
                "message_not_found" => new(MessageOperationStatus.MessageNotFound),
                "recipient_unavailable" => new(MessageOperationStatus.RecipientUnavailable),
                "reply_scope_mismatch" => new(MessageOperationStatus.ReplyScopeMismatch),
                "attachment_unavailable" => new(MessageOperationStatus.AttachmentUnavailable),
                _ => new(MessageOperationStatus.ValidationFailed, Errors: new Dictionary<string, string[]> { [exception.Code] = [exception.Code] })
            };
        }
        catch (ArgumentException) { return Invalid<MessageDto>("message"); }
        catch (RoundTableMeeting.Domain.Exceptions.MessageAppendConflictException)
        {
            // A concurrent winner is checked by the same complete-payload replay validation on retry.
            return new(MessageOperationStatus.Conflict);
        }
    }

    public async Task<MessageOperationResult<MessagePageDto>> GetPageAsync(Guid meetingId, MessagePageRequest request, CancellationToken ct = default)
    {
        if (currentUser.UserId is not Guid userId || userId == Guid.Empty) return new(MessageOperationStatus.Unauthenticated);
        if (request.BeforeSequence.HasValue && request.AfterSequence.HasValue || request.BeforeSequence.HasValue && request.ThroughSequence.HasValue || request.BeforeSequence < 0 || request.AfterSequence < 0 || request.ThroughSequence < 0 || request.ThroughSequence < request.AfterSequence) return Invalid<MessagePageDto>("cursor");
        using var sharedLease = await realtimeGate.AcquireSharedAsync(meetingId, ct);
        if (sharedLease is null) return new(await UnavailableStatus(meetingId, currentUser.UserId.GetValueOrDefault(), ct));
        var meeting = await meetings.FindActiveByIdAsync(meetingId, ct);
        var participant = meeting is null ? null : await participants.FindByUserAsync(meetingId, userId, ct);
        if (meeting is null) return new(await UnavailableStatus(meetingId, userId, ct));
        if (participant is null) return new(MessageOperationStatus.MeetingNotFound);
        var through = request.ThroughSequence;
        if (participant.MembershipState == Domain.Enums.MeetingMembershipState.Left)
            through = Math.Min(through ?? long.MaxValue, participant.LeftThroughSequence!.Value);
        var limit = request.Limit ?? options.HistoryPageSize; if (limit is < 1 || limit > options.MaxHistoryPageSize) return Invalid<MessagePageDto>("limit");
        if (request.Scope is not ("all" or "public" or "private") || request.Scope == "public" && request.PeerParticipantId.HasValue
            || request.PeerParticipantId == Guid.Empty || request.PeerParticipantId == participant.ID) return Invalid<MessagePageDto>("scope");
        if (request.PeerParticipantId is Guid peer && !(await participants.ListAsync(meetingId, ct)).Any(p => p.ID == peer))
            return Invalid<MessagePageDto>("peerParticipantId");
        var watermark = await visibility.HighWatermarkAsync(meetingId, participant.ID, ct);
        through = Math.Min(through ?? watermark, watermark);
        var rows = await visibility.PageAsync(meetingId, participant.ID, request.Scope, request.PeerParticipantId,
            request.BeforeSequence, request.AfterSequence, through, limit + 1, ct);
        var more = rows.Count > limit;
        var selected = request.AfterSequence.HasValue ? rows.Take(limit) : rows.TakeLast(limit);
        var dtos = new List<MessageDto>();
        foreach (var row in selected) dtos.Add(await mapper.MapAsync(row, ct));
        var first = dtos.FirstOrDefault()?.Sequence; var last = dtos.LastOrDefault()?.Sequence;
        return new(MessageOperationStatus.Success, new(dtos, !request.AfterSequence.HasValue && more,
            request.AfterSequence.HasValue && more, first, last, first, last) { VisibleThroughSequence = through.Value });
    }
    public async Task<MessageOperationResult<MeetingChatSubscriptionAccessDto>> AuthorizeAsync(Guid meetingId, Guid userId, CancellationToken ct = default)
    {
        if (userId == Guid.Empty) return new(MessageOperationStatus.Unauthenticated);
        var meeting = await meetings.FindActiveByIdAsync(meetingId, ct);
        var p = meeting is null ? null : await participants.FindByUserAsync(meetingId, userId, ct);
        if (meeting is null || p is null) return new(MessageOperationStatus.MeetingNotFound);
        if (p.MembershipState == MeetingMembershipState.Left) return new(MessageOperationStatus.MembershipLeft);
        return new(MessageOperationStatus.Success, new(p.ID, await visibility.HighWatermarkAsync(meetingId, p.ID, ct)));
    }
    public async Task<MessageOperationResult<MessageDto>> GetMessageAsync(Guid meetingId, Guid messageId, CancellationToken ct = default)
    {
        if (currentUser.UserId is not Guid userId || userId == Guid.Empty) return new(MessageOperationStatus.Unauthenticated);
        using var lease = await realtimeGate.AcquireSharedAsync(meetingId, ct);
        if (lease is null || await meetings.FindActiveByIdAsync(meetingId, ct) is null) return new(await UnavailableStatus(meetingId, userId, ct));
        var p = await participants.FindByUserAsync(meetingId, userId, ct);
        if (p is null) return new(MessageOperationStatus.MeetingNotFound);
        var row = await visibility.FindAsync(meetingId, p.ID, messageId, p.LeftThroughSequence, ct);
        return row is null ? new(MessageOperationStatus.MessageNotFound) : new(MessageOperationStatus.Success, await mapper.MapAsync(row, ct));
    }
    public async Task<MessageOperationResult<SubmissionStatusDto>> GetSubmissionAsync(Guid meetingId, Guid submissionId, CancellationToken ct = default)
    {
        if (currentUser.UserId is not Guid userId || userId == Guid.Empty) return new(MessageOperationStatus.Unauthenticated);
        using var lease = await realtimeGate.AcquireSharedAsync(meetingId, ct);
        if (lease is null || await meetings.FindActiveByIdAsync(meetingId, ct) is null) return new(await UnavailableStatus(meetingId, userId, ct));
        var p = await participants.FindByUserAsync(meetingId, userId, ct);
        if (p is null) return new(MessageOperationStatus.MeetingNotFound);
        var message = await messages.FindBySubmissionAsync(meetingId, p.ID, submissionId, ct);
        var row = message is null ? null : await visibility.FindAsync(meetingId, p.ID, message.ID, p.LeftThroughSequence, ct);
        return new(MessageOperationStatus.Success, row is null ? new("not_found", null) : new("saved", await mapper.MapAsync(row, ct)));
    }
    private async Task<MessageOperationStatus> UnavailableStatus(Guid meetingId, Guid userId, CancellationToken cancellationToken)
        => await lifecycleService.GetMemberStateAsync(meetingId, userId, cancellationToken) switch
        {
            "deleting" => MessageOperationStatus.MeetingDeleting,
            "deleted" => MessageOperationStatus.MeetingDeleted,
            _ => MessageOperationStatus.MeetingNotFound
        };
    private static MessageOperationResult<T> Invalid<T>(string field) => new(MessageOperationStatus.ValidationFailed, Errors: new Dictionary<string,string[]> { [field] = ["The value is invalid."] });
}
