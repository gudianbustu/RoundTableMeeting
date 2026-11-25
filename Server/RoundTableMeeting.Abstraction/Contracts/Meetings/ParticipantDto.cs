using RoundTableMeeting.Abstraction.Enums;
namespace RoundTableMeeting.Abstraction.Contracts.Meetings;
public sealed record ParticipantDto(Guid ParticipantId, string DisplayName, ParticipantKind Type, bool IsCurrentUser, bool IsCreator, DateTimeOffset JoinedAt)
{
    public string? AvatarUrl { get; init; }
    public int? AvatarColorSlot { get; init; }
    public string MembershipState { get; init; } = "joined";
}
