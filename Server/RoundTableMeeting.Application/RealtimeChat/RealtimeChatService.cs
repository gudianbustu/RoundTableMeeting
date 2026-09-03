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
public sealed class RealtimeChatService(ICurrentUserContext currentUser, IMeetingRepository meetings, IMeetingParticipantRepository participants, IMessageRepository messages, IUnitOfWork unitOfWork, IMeetingTransactionRunner runner, IMessageMentionParser parser, IMessageEventConsumerCatalog consumerCatalog, RealtimeChatOptions options, IMeetingRealtimeGate realtimeGate, TimeProvider timeProvider) : IMessageAppendService, IMessageHistoryService, IMeetingChatSubscriptionService, IScopedDependency<IMessageAppendService>, IScopedDependency<IMessageHistoryService>, IScopedDependency<IMeetingChatSubscriptionService>
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
        if (sharedLease is null) return new(MessageOperationStatus.MeetingNotFound);
        var effectiveUserId = currentUser.UserId.GetValueOrDefault();
        if (request.ClientSubmissionId == Guid.Empty) return Invalid<MessageDto>("clientSubmissionId");
        var meeting = await meetings.FindActiveByIdAsync(meetingId, ct); if (meeting is null) return new(MessageOperationStatus.MeetingNotFound);
        var participant = kind == DomainAuthorKind.System ? null : await participants.FindByUserAsync(meetingId, effectiveUserId, ct); if (kind != DomainAuthorKind.System && participant is null) return new(MessageOperationStatus.MeetingNotFound);
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
                MeetingMessage.ValidateContentLength(content, options.MaxContentLength);
                var sequence = await messages.GetNextSequenceAsync(meetingId, token); var authorName = kind == DomainAuthorKind.System ? (systemName ?? "System") : participant!.DisplayName;
                var entity = kind == DomainAuthorKind.System ? MeetingMessage.CreateSystem(meetingId, sequence, authorName, content, request.ClientSubmissionId, timeProvider.GetUtcNow()) : MeetingMessage.CreateParticipant(meetingId, sequence, participant!.ID, kind, authorName, content, request.ClientSubmissionId, timeProvider.GetUtcNow());
                unitOfWork.RegisterAdd(entity);
                if (kind != DomainAuthorKind.System)
                {
                    var people = (await participants.ListAsync(meetingId, token)).Select(p => new MentionParticipant(p.ID, p.DisplayName, p.NormalizedDisplayName)).ToArray();
                    foreach (var mention in parser.Parse(content, people, request.MentionCandidates))
                        unitOfWork.RegisterAdd(MessageMention.Create(meetingId, entity.ID, mention.ParticipantId, mention.DisplayName, mention.StartOffset, mention.Length));
                }
                var appendedEvent = MessageAppendedEvent.Create(meetingId, entity.ID, sequence, entity.CreatedAtUtc);
                unitOfWork.RegisterAdd(appendedEvent);
                foreach (var consumerName in consumerCatalog.ConsumerNames)
                    unitOfWork.RegisterAdd(MessageEventDelivery.Create(appendedEvent.ID, consumerName, entity.CreatedAtUtc));
                return entity;
            }, ct);
            return new(MessageOperationStatus.Created, await ToDto(message, ct));
        }
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
        var meeting = await meetings.FindActiveByIdAsync(meetingId, ct); if (meeting is null || await participants.FindByUserAsync(meetingId, userId, ct) is null) return new(MessageOperationStatus.MeetingNotFound);
        var limit = request.Limit ?? options.HistoryPageSize; if (limit is < 1 || limit > options.MaxHistoryPageSize) return Invalid<MessagePageDto>("limit");
        var rows = await messages.ListAsync(meetingId, request.BeforeSequence, request.AfterSequence, request.ThroughSequence, limit, ct); var dtos = new List<MessageDto>(); foreach (var row in rows) dtos.Add(await ToDto(row, ct));
        var first = dtos.FirstOrDefault()?.Sequence; var last = dtos.LastOrDefault()?.Sequence; var before = !request.AfterSequence.HasValue && first.HasValue && await messages.HasBeforeAsync(meetingId, first.Value, ct); var after = request.AfterSequence.HasValue && last.HasValue && await messages.HasAfterAsync(meetingId, last.Value, request.ThroughSequence, ct);
        return new(MessageOperationStatus.Success, new(dtos, before, after, first, last, first, last));
    }
    public async Task<MessageOperationResult<MeetingChatSubscriptionAccessDto>> AuthorizeAsync(Guid meetingId, Guid userId, CancellationToken ct = default)
    { if (userId == Guid.Empty) return new(MessageOperationStatus.Unauthenticated); var meeting = await meetings.FindActiveByIdAsync(meetingId, ct); var p = meeting is null ? null : await participants.FindByUserAsync(meetingId, userId, ct); if (meeting is null || p is null) return new(MessageOperationStatus.MeetingNotFound); var rows = await messages.ListAsync(meetingId, null, null, null, 1, ct); return new(MessageOperationStatus.Success, new(p.ID, rows.LastOrDefault()?.Sequence ?? 0)); }
    private async Task<MessageDto> ToDto(MeetingMessage message, CancellationToken ct) { var rows = await messages.ListMentionsAsync(message.ID, ct); var mentions = rows.Select(m => new MessageMentionDto(m.TargetParticipantId, m.TargetDisplayName, m.StartOffset, m.Length)).ToArray(); return new(message.ID, message.MeetingId, message.Sequence, new(message.AuthorParticipantId, message.AuthorKind switch { DomainAuthorKind.Agent => TransportAuthorKind.Agent, DomainAuthorKind.System => TransportAuthorKind.System, _ => TransportAuthorKind.Human }, message.AuthorDisplayName), message.Content, mentions, message.CreatedAtUtc); }
    private static MessageOperationResult<T> Invalid<T>(string field) => new(MessageOperationStatus.ValidationFailed, Errors: new Dictionary<string,string[]> { [field] = ["The value is invalid."] });
}
