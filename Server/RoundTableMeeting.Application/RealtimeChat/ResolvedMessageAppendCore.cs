using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Domain.Exceptions;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Domain.UnitOfWork;
using DomainAuthorKind = RoundTableMeeting.Domain.Enums.MessageAuthorKind;
using TransportAuthorKind = RoundTableMeeting.Abstraction.Contracts.RealtimeChat.MessageAuthorKind;
namespace RoundTableMeeting.Application.RealtimeChat;

public sealed class ResolvedMessageAppendCore(IMeetingRepository meetings, IMeetingParticipantRepository participants,
    IMessageRepository messages, IMessageVisibilityRepository visibility, IMeetingAttachmentRepository attachments,
    IUnitOfWork unitOfWork, IMeetingDiscussionRepository discussion, IMessageMentionParser parser, IMessageEventConsumerCatalog consumerCatalog,
    RealtimeChatOptions options, TimeProvider timeProvider) : IResolvedMessageAppendCore
{
    public Task<ResolvedMessageAppendResult> AppendAgentAsync(Guid meetingId, Guid participantId, string authorDisplayName,
        string content, Guid clientSubmissionId, CancellationToken cancellationToken = default)
        => AppendAsync(meetingId, new(participantId, authorDisplayName, TransportAuthorKind.Agent), content, clientSubmissionId, cancellationToken: cancellationToken);
    public Task<ResolvedMessageAppendResult> AppendAsync(Guid meetingId, ResolvedMessageAuthor author, string content,
        Guid clientSubmissionId, IReadOnlyList<MentionCandidateDto>? mentionCandidates = null, CancellationToken cancellationToken = default)
        => AppendScopedAsync(meetingId, author, new(clientSubmissionId, content, mentionCandidates), cancellationToken);

    public async Task<ResolvedMessageAppendResult> AppendScopedAsync(Guid meetingId, ResolvedMessageAuthor author, SubmitMessageRequest request, CancellationToken ct = default)
    {
        if (meetingId == Guid.Empty || request.ClientSubmissionId == Guid.Empty || !Enum.IsDefined(author.Kind)
            || (author.Kind == TransportAuthorKind.System ? author.ParticipantId is not null : author.ParticipantId is null || author.ParticipantId == Guid.Empty))
            throw new ArgumentException("Message identity is invalid.");
        if (await meetings.FindActiveByIdAsync(meetingId, ct) is null) throw new MeetingMessageAccessException("meeting_not_found");
        var roster = await participants.ListAsync(meetingId, ct);
        var sender = roster.SingleOrDefault(p => p.ID == author.ParticipantId);
        if (author.Kind != TransportAuthorKind.System && sender is null) throw new MeetingMessageAccessException("meeting_not_found");
        if (sender?.MembershipState == MeetingMembershipState.Left) throw new MeetingMessageAccessException("membership_left");
        var scope = request.Visibility;
        var recipient = request.RecipientParticipantId;
        if (request.ReplyToMessageId is Guid replyId)
        {
            var original = sender is null ? null : await visibility.FindAsync(meetingId, sender.ID, replyId, ct: ct);
            if (original is null) throw new MeetingMessageAccessException("message_not_found");
            var replyPeer = original.Message.Visibility == "private"
                ? original.Message.AuthorParticipantId == sender!.ID ? original.Message.RecipientParticipantId : original.Message.AuthorParticipantId : null;
            if (scope is not null && scope != original.Message.Visibility || recipient is not null && recipient != replyPeer)
                throw new MeetingMessageAccessException("reply_scope_mismatch");
            scope = original.Message.Visibility; recipient = replyPeer;
        }
        scope ??= "public";
        if (scope is not ("public" or "private") || scope == "public" && recipient is not null) throw new ArgumentException("Invalid scope.");
        var ids = request.AttachmentIds?.ToArray() ?? [];
        if (request.OriginalTextAttachmentId is Guid originalId && !ids.Contains(originalId)) throw new ArgumentException("Missing original text attachment.");
        if (ids.Length > (request.OriginalTextAttachmentId.HasValue ? 21 : 20) || ids.Distinct().Count() != ids.Length || ids.Any(id => id == Guid.Empty)) throw new ArgumentException("Invalid attachments.");
        var content = request.Content ?? "";
        if (string.IsNullOrWhiteSpace(content) && ids.Length == 0) throw new ArgumentException("Content or attachment is required.");
        MeetingMessage.ValidateContentLength(content, options.MaxContentLength);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
            content, scope, recipient, attachments = ids.Order().ToArray(), replyTo = request.ReplyToMessageId,
            mentions = request.MentionCandidates ?? [] }))));
        if (request.SourceTextDraftId is Guid draft)
            hash = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes($"{hash}:text-draft:{draft:N}")));
        if (request.SourcePendingMessageId is Guid pending)
            hash = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes($"{hash}:pending-reply:{pending:N}")));
        var legacyHash = hash;
        if (request.SourceRecipient is { } selector)
        {
            // Keep the original MCP address form in the receipt, even if that nickname later changes hands.
            hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { hash, selector.RequestedName })));
        }
        var existing = sender is null ? await messages.FindSystemBySubmissionAsync(meetingId, request.ClientSubmissionId, ct)
            : await messages.FindBySubmissionAsync(meetingId, sender.ID, request.ClientSubmissionId, ct);
        if (existing is not null)
        {
            if (existing.OriginalTextAttachmentId != request.OriginalTextAttachmentId) throw new MeetingMessageAccessException("submission_mismatch");
            var matchesLegacyRecipient = request.SourceRecipient is { } previousSelector
                && previousSelector.CurrentlyResolvedId == existing.RecipientParticipantId && existing.PayloadFingerprint == legacyHash;
            if (existing.PayloadFingerprint != hash && !matchesLegacyRecipient && !(existing.PayloadFingerprint == "" && scope == "public" && ids.Length == 0
                && request.ReplyToMessageId is null && existing.Content == content)) throw new MeetingMessageAccessException("submission_mismatch");
            return Result(existing) with { Replayed = true };
        }
        // Replay of an already committed submission takes precedence over a later mode change.
        if (author.Kind == TransportAuthorKind.Agent && request.ReplyToMessageId is Guid parentId
            && await discussion.FindTurnAsync(parentId, ct) is { Optional: true } parent
            && await discussion.WasInterruptedAsync(meetingId, parent.Sequence, ct))
            throw new MeetingMessageAccessException("discussion_ended");
        IReadOnlyList<ParsedMention> mentions = [];
        if (sender is not null)
        {
            var people = roster.Where(p => p.MembershipState == MeetingMembershipState.Joined && (scope == "public" || p.ID == sender.ID || p.ID == recipient))
                .Select(p => new MentionParticipant(p.ID, p.DisplayName, p.NormalizedDisplayName)).ToArray();
            mentions = parser.Parse(content, people, request.MentionCandidates);
        }
        if (author.Kind == TransportAuthorKind.Agent && scope == "public" && mentions.Count == 0 && request.ReplyToMessageId is null
            && !request.StartsPublicTopic && await discussion.WasInterruptedAsync(meetingId, 0, ct))
            throw new MeetingMessageAccessException("reply_reference_required");
        if (scope == "private" && (sender is null || recipient == sender.ID
            || !roster.Any(p => p.ID == recipient && p.MembershipState == MeetingMembershipState.Joined)))
            throw new MeetingMessageAccessException("recipient_unavailable");
        var sequence = await messages.GetNextSequenceAsync(meetingId, ct);
        var message = sender is null ? MeetingMessage.CreateSystem(meetingId, sequence, author.DisplayName, content, request.ClientSubmissionId, timeProvider.GetUtcNow())
            : MeetingMessage.CreateParticipant(meetingId, sequence, sender.ID,
                author.Kind == TransportAuthorKind.Agent ? DomainAuthorKind.Agent : DomainAuthorKind.Human,
                sender.DisplayName, content, request.ClientSubmissionId, timeProvider.GetUtcNow());
        if (request.OriginalTextAttachmentId is Guid fullTextId) message.SetOriginalText(fullTextId);
        message.SetScope(scope, recipient, request.ReplyToMessageId, hash);
        unitOfWork.RegisterAdd(message);
        foreach (var id in ids)
        {
            var file = await attachments.FindAsync(meetingId, id, ct);
            if (file is null || file.UploaderParticipantId != sender?.ID || file.State != "staged") throw new MeetingMessageAccessException("attachment_unavailable");
            file.Bind(message.ID); unitOfWork.RegisterEdit(file);
        }
        if (sender is not null)
        {
            if (scope == "public" && await discussion.ClassifyAsync(message, mentions.Count != 0, request.StartsPublicTopic, ct) is { } turn)
                unitOfWork.RegisterAdd(turn);
            foreach (var mention in mentions)
                unitOfWork.RegisterAdd(MessageMention.Create(meetingId, message.ID, mention.ParticipantId, mention.DisplayName, mention.StartOffset, mention.Length));
        }
        var evt = MessageAppendedEvent.Create(meetingId, message.ID, sequence, message.CreatedAtUtc);
        unitOfWork.RegisterAdd(evt);
        foreach (var consumer in consumerCatalog.ConsumerNames) unitOfWork.RegisterAdd(MessageEventDelivery.Create(evt.ID, consumer, message.CreatedAtUtc));
        return Result(message);
    }
    private static ResolvedMessageAppendResult Result(MeetingMessage m) => new(m.ID, m.MeetingId, m.Sequence, m.AuthorParticipantId, m.AuthorDisplayName, m.Content, m.CreatedAtUtc);
}
