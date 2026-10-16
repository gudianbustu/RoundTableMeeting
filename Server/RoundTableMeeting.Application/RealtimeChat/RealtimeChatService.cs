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
public sealed class RealtimeChatService(ICurrentUserContext currentUser, IMeetingRepository meetings, IMeetingParticipantRepository participants, IMessageRepository messages, IMeetingTransactionRunner runner, IResolvedMessageAppendCore appendCore, RealtimeChatOptions options, IMeetingRealtimeGate realtimeGate, IMeetingLifecycleService lifecycleService) : IMessageAppendService, IMessageHistoryService, IMeetingChatSubscriptionService, IScopedDependency<IMessageAppendService>, IScopedDependency<IMessageHistoryService>, IScopedDependency<IMeetingChatSubscriptionService>
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
        var content = (request.Content ?? string.Empty).Normalize(NormalizationForm.FormC).Trim();
        if (content.Length == 0) return Invalid<MessageDto>("content");
        var existing = kind == DomainAuthorKind.System
            ? await messages.FindSystemBySubmissionAsync(meetingId, request.ClientSubmissionId, ct)
            : await messages.FindBySubmissionAsync(meetingId, participant!.ID, request.ClientSubmissionId, ct);
        if (existing is not null) return string.Equals(existing.Content, content, StringComparison.Ordinal) ? new(MessageOperationStatus.Replayed, await ToDto(existing, ct)) : new(MessageOperationStatus.SubmissionMismatch);
        try
        {
            var message = await runner.RunAsync(async token =>
            {
                var author = kind == DomainAuthorKind.System
                    ? new ResolvedMessageAuthor(null, systemName ?? "System", TransportAuthorKind.System)
                    : new ResolvedMessageAuthor(participant!.ID, participant.DisplayName, TransportAuthorKind.Human);
                return await appendCore.AppendAsync(meetingId, author, content, request.ClientSubmissionId, request.MentionCandidates, token);
            }, ct);
            var persisted = await messages.FindByIdAsync(meetingId, message.MessageId, ct)
                ?? throw new InvalidOperationException("The committed message could not be read.");
            return new(MessageOperationStatus.Created, await ToDto(persisted, ct));
        }
        catch (RoundTableMeeting.Domain.Exceptions.MeetingMessageAccessException exception)
        { return new(exception.Code == "membership_left" ? MessageOperationStatus.MembershipLeft : MessageOperationStatus.MeetingNotFound); }
        catch (ArgumentException) { return Invalid<MessageDto>("content"); }
        catch (RoundTableMeeting.Domain.Exceptions.MessageAppendConflictException)
        {
            var replay = kind == DomainAuthorKind.System
                ? await messages.FindSystemBySubmissionAsync(meetingId, request.ClientSubmissionId, ct)
                : await messages.FindBySubmissionAsync(meetingId, participant!.ID, request.ClientSubmissionId, ct);
            if (replay is null) return new(MessageOperationStatus.Conflict);
            return string.Equals(replay.Content, content, StringComparison.Ordinal)
                ? new(MessageOperationStatus.Replayed, await ToDto(replay, ct))
                : new(MessageOperationStatus.SubmissionMismatch);
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
        var rows = await messages.ListAsync(meetingId, request.BeforeSequence, request.AfterSequence, through, limit, ct); var dtos = new List<MessageDto>(); foreach (var row in rows) dtos.Add(await ToDto(row, ct));
        var first = dtos.FirstOrDefault()?.Sequence; var last = dtos.LastOrDefault()?.Sequence; var before = !request.AfterSequence.HasValue && first.HasValue && await messages.HasBeforeAsync(meetingId, first.Value, ct); var after = request.AfterSequence.HasValue && last.HasValue && await messages.HasAfterAsync(meetingId, last.Value, through, ct);
        return new(MessageOperationStatus.Success, new(dtos, before, after, first, last, first, last));
    }
    public async Task<MessageOperationResult<MeetingChatSubscriptionAccessDto>> AuthorizeAsync(Guid meetingId, Guid userId, CancellationToken ct = default)
    { if (userId == Guid.Empty) return new(MessageOperationStatus.Unauthenticated); var meeting = await meetings.FindActiveByIdAsync(meetingId, ct); var p = meeting is null ? null : await participants.FindByUserAsync(meetingId, userId, ct); if (meeting is null || p is null) return new(MessageOperationStatus.MeetingNotFound); if (p.MembershipState == Domain.Enums.MeetingMembershipState.Left) return new(MessageOperationStatus.MembershipLeft); var rows = await messages.ListAsync(meetingId, null, null, null, 1, ct); return new(MessageOperationStatus.Success, new(p.ID, rows.LastOrDefault()?.Sequence ?? 0)); }
    private async Task<MessageDto> ToDto(MeetingMessage message, CancellationToken ct) { var rows = await messages.ListMentionsAsync(message.MeetingId, message.ID, ct); var mentions = rows.Select(m => new MessageMentionDto(m.TargetParticipantId, m.TargetDisplayName, m.StartOffset, m.Length)).ToArray(); return new(message.ID, message.MeetingId, message.Sequence, new(message.AuthorParticipantId, message.AuthorKind switch { DomainAuthorKind.Agent => TransportAuthorKind.Agent, DomainAuthorKind.System => TransportAuthorKind.System, _ => TransportAuthorKind.Human }, message.AuthorDisplayName), message.Content, mentions, message.CreatedAtUtc); }
    private async Task<MessageOperationStatus> UnavailableStatus(Guid meetingId, Guid userId, CancellationToken cancellationToken)
        => await lifecycleService.GetMemberStateAsync(meetingId, userId, cancellationToken) switch
        {
            "deleting" => MessageOperationStatus.MeetingDeleting,
            "deleted" => MessageOperationStatus.MeetingDeleted,
            _ => MessageOperationStatus.MeetingNotFound
        };
    private static MessageOperationResult<T> Invalid<T>(string field) => new(MessageOperationStatus.ValidationFailed, Errors: new Dictionary<string,string[]> { [field] = ["The value is invalid."] });
}
