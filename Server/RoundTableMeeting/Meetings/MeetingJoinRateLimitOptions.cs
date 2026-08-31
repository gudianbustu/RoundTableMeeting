using System.ComponentModel.DataAnnotations;

namespace RoundTableMeeting.Meetings;

public sealed class MeetingJoinRateLimitOptions
{
    public const string SectionName = "MeetingJoinRateLimit";
    [Range(1, int.MaxValue)] public int WindowSeconds { get; set; } = 300;
    [Range(1, int.MaxValue)] public int PerUserLimit { get; set; } = 30;
    [Range(1, int.MaxValue)] public int PerSourceLimit { get; set; } = 100;
}
