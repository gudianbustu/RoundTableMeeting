using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Repository.Sqlite.Database;
namespace RoundTableMeeting.IntegrationTests;

internal static class LegacyMessageSeed
{
    public static Task InsertAsync(RoundTableMeetingDbContext db, MeetingMessage message)
        => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO MeetingMessages (ID, MeetingId, Sequence, AuthorParticipantId, AuthorKind, AuthorDisplayName, Content, ClientSubmissionId, CreatedAtUtc)
            VALUES ({message.ID}, {message.MeetingId}, {message.Sequence}, {message.AuthorParticipantId}, {message.AuthorKind.ToString()},
                    {message.AuthorDisplayName}, {message.Content}, {message.ClientSubmissionId}, {message.CreatedAtUtc})
            """);
    public static IQueryable<MeetingMessage> Query(RoundTableMeetingDbContext db) => db.MeetingMessages.FromSqlRaw("""
        SELECT m.*, '' AS PayloadFingerprint, 'public' AS Visibility, NULL AS RecipientParticipantId, NULL AS ReplyToMessageId
        FROM MeetingMessages m
        """).AsNoTracking();
}
