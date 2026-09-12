using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.Domain.Entities;

public sealed partial class AgentReplyTask
{
    public static AgentReplyTask CreateDerived(
        Guid taskId, Guid agentParticipantId, MeetingMessage trigger, MessageMention mention,
        AgentReplyTask parent, long candidateFromSequenceExclusive, Guid replyIdempotencyKey,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(mention);
        ArgumentNullException.ThrowIfNull(parent);
        if (trigger.AuthorKind != MessageAuthorKind.Agent
            || parent.MeetingId != trigger.MeetingId
            || parent.State != AgentReplyTaskState.Replied
            || parent.ReplyMessageId != trigger.ID
            || parent.AgentParticipantId != trigger.AuthorParticipantId
            || parent.TriggerSequence >= trigger.Sequence
            || parent.ChainId == Guid.Empty
            || parent.SourceAuthorKind == MessageAuthorKind.System
            || (parent.SourceAuthorKind == MessageAuthorKind.Human
                && (parent.ParentTaskId.HasValue || parent.ChainId != parent.TriggerMessageId))
            || (parent.SourceAuthorKind == MessageAuthorKind.Agent && !parent.ParentTaskId.HasValue))
            throw new ArgumentException("The trigger must be the verified reply of a task in the same meeting.", nameof(parent));
        if (mention.MeetingId != trigger.MeetingId || mention.MessageId != trigger.ID
            || mention.TargetParticipantId != agentParticipantId)
            throw new ArgumentException("The mention must belong to the trigger and target participant.", nameof(mention));

        return new AgentReplyTask(taskId, trigger.MeetingId, agentParticipantId, trigger.ID,
            trigger.Sequence, mention.ID, trigger.AuthorKind, trigger.AuthorParticipantId,
            candidateFromSequenceExclusive, replyIdempotencyKey, createdAtUtc)
        {
            ChainId = parent.ChainId,
            ParentTaskId = parent.ID
        };
    }
}
