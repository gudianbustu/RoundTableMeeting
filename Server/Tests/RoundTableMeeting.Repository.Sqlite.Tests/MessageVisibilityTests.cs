using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;
using RoundTableMeeting.Repository.Sqlite.Repositories;
namespace RoundTableMeeting.Repository.Sqlite.Tests;

public sealed class MessageVisibilityTests
{
    [Fact]
    public async Task PrivateInsertDoesNotChangeOtherParticipantsCursorAndNewMemberGetsOnlyPublicHistory()
    {
        var path = Path.Combine(Path.GetTempPath(), "message-projection-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await using var db = new RoundTableMeetingDbContext(new DbContextOptionsBuilder<RoundTableMeetingDbContext>()
                .UseSqlite($"Data Source={path};Foreign Keys=True;Pooling=False").Options);
            await db.Database.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
            var now = DateTimeOffset.UtcNow;
            var owner = UserAccount.Create("owner", "Owner"); owner.InitializePasswordHash("hash");
            var meeting = Meeting.Create("Projection", "", owner.ID, now);
            db.AddRange(owner, meeting); await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            var a = MeetingParticipant.CreateHuman(meeting.ID, owner.ID, "Owner", now);
            var b = MeetingParticipant.CreateAgent(meeting.ID, "Peer", now);
            var c = MeetingParticipant.CreateAgent(meeting.ID, "Other", now);
            db.AddRange(a, b, c); await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            var publicOne = MeetingMessage.CreateParticipant(meeting.ID, 1, a.ID, MessageAuthorKind.Human, a.DisplayName, "public", Guid.NewGuid(), now);
            db.Add(publicOne); await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            var secret = MeetingMessage.CreateParticipant(meeting.ID, 2, a.ID, MessageAuthorKind.Human, a.DisplayName, "private", Guid.NewGuid(), now);
            secret.SetScope("private", b.ID, null, new string('A', 64));
            db.Add(secret); await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            var repo = new MessageVisibilityRepository(db);
            Assert.Equal(2, await repo.HighWatermarkAsync(meeting.ID, a.ID, TestContext.Current.CancellationToken));
            Assert.Equal(2, await repo.HighWatermarkAsync(meeting.ID, b.ID, TestContext.Current.CancellationToken));
            Assert.Equal(1, await repo.HighWatermarkAsync(meeting.ID, c.ID, TestContext.Current.CancellationToken));
            Assert.Null(await repo.FindAsync(meeting.ID, c.ID, secret.ID, ct: TestContext.Current.CancellationToken));
            Assert.Equal(2, (await repo.FindAsync(meeting.ID, b.ID, secret.ID, ct: TestContext.Current.CancellationToken))!.Sequence);
            var publicTwo = MeetingMessage.CreateParticipant(meeting.ID, 3, a.ID, MessageAuthorKind.Human, a.DisplayName, "public two", Guid.NewGuid(), now);
            db.Add(publicTwo); await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            var outsiderPage = await repo.PageAsync(meeting.ID, c.ID, "all", null, null, 0, null, 100, TestContext.Current.CancellationToken);
            Assert.Equal(new long[] { 1, 2 }, outsiderPage.Select(x => x.Sequence));
            Assert.Equal(new[] { publicOne.ID, publicTwo.ID }, outsiderPage.Select(x => x.Message.ID));
            var newcomer = MeetingParticipant.CreateAgent(meeting.ID, "New", now);
            db.Add(newcomer); await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, await repo.HighWatermarkAsync(meeting.ID, newcomer.ID, TestContext.Current.CancellationToken));
            Assert.Null(await repo.FindAsync(meeting.ID, newcomer.ID, secret.ID, ct: TestContext.Current.CancellationToken));
            Assert.Equal(0, await db.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.MigrateAsync("20261015131414_AddMeetingMembershipLifecycle", cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(2, await repo.HighWatermarkAsync(meeting.ID, c.ID, TestContext.Current.CancellationToken));
        }
        finally { File.Delete(path); }
    }
}
