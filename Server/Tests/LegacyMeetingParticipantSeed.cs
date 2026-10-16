using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Repository.Sqlite.Database;
namespace RoundTableMeeting.Tests;

// Historical-schema fixtures must not use current EF columns before applying the new migration.
internal static class LegacyMeetingParticipantSeed
{
    public static Task InsertAsync(RoundTableMeetingDbContext db, MeetingParticipant participant)
        => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO MeetingParticipants (ID, MeetingId, UserId, ParticipantType, DisplayName, NormalizedDisplayName, JoinedAtUtc)
            VALUES ({participant.ID}, {participant.MeetingId}, {participant.UserId}, {participant.ParticipantType.ToString()},
                    {participant.DisplayName}, {participant.NormalizedDisplayName}, {participant.JoinedAtUtc})
            """);
}
