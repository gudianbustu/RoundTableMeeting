using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;
using RoundTableMeeting.Domain.Entities;

namespace RoundTableMeeting.Application.AgentCollaboration;

public sealed partial class AgentCollaborationService
{
    private static AgentCollaborationSourceDto CollaborationFor(AgentReplyTask task)
        => new(task.ChainId, task.ParentTaskId, task.SourceAuthorKind.ToString().ToLowerInvariant(), task.SourceParticipantId);
}
