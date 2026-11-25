using Materal.Extensions.DependencyInjection;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Repositories;
namespace RoundTableMeeting.Application.RealtimeChat;
public sealed class VisibleMessageMapper(IMessageRepository messages, IMeetingAttachmentRepository attachments, IMeetingParticipantRepository participants, IMeetingDiscussionRepository discussion) : IScopedDependency<VisibleMessageMapper>
{
    public async Task<MessageDto> MapAsync(VisibleMessage row, CancellationToken ct = default)
    {
        var m = row.Message;
        var mentions = await messages.ListMentionsAsync(m.MeetingId, m.ID, ct);
        var files = await attachments.ForMessageAsync(m.MeetingId, m.ID, ct);
        var recipient = m.RecipientParticipantId is Guid id ? (await participants.ListAsync(m.MeetingId, ct)).SingleOrDefault(p => p.ID == id) : null;
        var turn = m.Visibility == "public" ? await discussion.FindTurnAsync(m.ID, ct) : null;
        var interrupted = turn?.Optional == true && await discussion.WasInterruptedAsync(m.MeetingId, turn.Sequence, ct);
        return new(m.ID, m.MeetingId, row.Sequence, new(m.AuthorParticipantId, (MessageAuthorKind)(int)m.AuthorKind, row.CurrentAuthorName ?? m.AuthorDisplayName), m.Content,
            mentions.Select(x => new MessageMentionDto(x.TargetParticipantId, x.TargetDisplayName, x.StartOffset, x.Length) { IsGroup = x.IsGroup }).ToArray(), m.CreatedAtUtc)
        { OriginalTextAttachmentId = m.OriginalTextAttachmentId, DiscussionTopicId = turn?.TopicId, OptionalAgentResponse = turn?.Optional == true && !interrupted, DiscussionInterrupted = interrupted,
            Visibility = m.Visibility, Recipient = recipient is null ? null : new(recipient.ID, recipient.DisplayName),
            Attachments = files.Select(x => new AttachmentDto(x.ID, x.OriginalName, x.SizeBytes, x.MediaType, x.State)).ToArray(), ReplyToMessageId = m.ReplyToMessageId };
    }
}
