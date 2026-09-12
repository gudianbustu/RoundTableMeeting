using System.Text.Json.Serialization;

namespace RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;

public sealed record AgentCollaborationSourceDto(Guid ChainId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? ParentTaskId,
    string SourceAuthorKind, Guid? SourceParticipantId);

public sealed partial record AgentMentionResultDto
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentCollaborationSourceDto? Collaboration { get; init; }
}

public sealed partial record AgentTaskSummaryDto
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentCollaborationSourceDto? Collaboration { get; init; }
}
