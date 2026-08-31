using System.Text;
using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.Domain.Entities;

public sealed class MeetingParticipant : BaseEntity
{
    public Guid MeetingId { get; private set; }
    public Guid? UserId { get; private set; }
    public ParticipantType ParticipantType { get; private set; }
    public string DisplayName { get; private set; } = string.Empty;
    public string NormalizedDisplayName { get; private set; } = string.Empty;
    public DateTimeOffset JoinedAtUtc { get; private set; }
    public MeetingParticipant() { }
    private MeetingParticipant(Guid meetingId, Guid? userId, ParticipantType type, string displayName, DateTimeOffset joinedAtUtc)
    {
        if (meetingId == Guid.Empty) throw new ArgumentException("Meeting is required.", nameof(meetingId));
        if (type == ParticipantType.Human && (!userId.HasValue || userId.Value == Guid.Empty)) throw new ArgumentException("Human participant requires a user.", nameof(userId));
        if (type == ParticipantType.Agent && userId.HasValue) throw new ArgumentException("Agent participant cannot have a user.", nameof(userId));
        DisplayName = NormalizeDisplayName(displayName);
        NormalizedDisplayName = DisplayName.ToUpperInvariant();
        if (NormalizedDisplayName.Length > 128) throw new ArgumentException("Normalized participant name is too long.", nameof(displayName));
        MeetingId = meetingId; UserId = userId; ParticipantType = type; JoinedAtUtc = joinedAtUtc;
    }
    public static MeetingParticipant CreateHuman(Guid meetingId, Guid userId, string displayName, DateTimeOffset joinedAtUtc)
        => new(meetingId, userId, ParticipantType.Human, displayName, joinedAtUtc);
    public static MeetingParticipant CreateAgent(Guid meetingId, string displayName, DateTimeOffset joinedAtUtc)
        => new(meetingId, null, ParticipantType.Agent, displayName, joinedAtUtc);
    public static string NormalizeDisplayName(string value)
    {
        var normalized = (value ?? string.Empty).Trim().Normalize(NormalizationForm.FormC);
        if (normalized.Length is < 1 or > 64) throw new ArgumentException("Participant name length is invalid.", nameof(value));
        return normalized;
    }
}
