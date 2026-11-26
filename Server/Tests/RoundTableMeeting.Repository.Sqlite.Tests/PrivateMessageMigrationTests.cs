using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;
namespace RoundTableMeeting.Repository.Sqlite.Tests;

public sealed class PrivateMessageMigrationTests
{
    [Fact]
    public async Task UpgradeBackfillsDensePositionsAndPreservesExitBoundsAndSafeDowngrade()
    {
        var path = Path.Combine(Path.GetTempPath(), "private-upgrade-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await using var db = new RoundTableMeetingDbContext(new DbContextOptionsBuilder<RoundTableMeetingDbContext>()
                .UseSqlite($"Data Source={path};Foreign Keys=True;Pooling=False").Options);
            const string previous = "20261015131414_AddMeetingMembershipLifecycle";
            await db.Database.MigrateAsync(previous, cancellationToken: TestContext.Current.CancellationToken);
            var now = DateTimeOffset.UtcNow;
            var user = UserAccount.Create("owner", "Owner"); user.InitializePasswordHash("hash");
            var meeting = Meeting.Create("Upgrade", null, user.ID, now);
            db.AddRange(user, meeting); await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            var author = MeetingParticipant.CreateHuman(meeting.ID, user.ID, "Owner", now);
            var left = MeetingParticipant.CreateAgent(meeting.ID, "Left", now); left.Leave(4);
            await LegacyMeetingParticipantSeed.InsertAsync(db, author);
            await LegacyMeetingParticipantSeed.InsertAsync(db, left);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE MeetingParticipants SET MembershipState = 'Left', LeftThroughSequence = {left.LeftThroughSequence}, MembershipRevision = {left.MembershipRevision} WHERE ID = {left.ID}", cancellationToken: TestContext.Current.CancellationToken);
            db.Add(MeetingLeaveOperation.Create(left, Guid.NewGuid())); await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            var messages = new[] { 1L, 4L, 7L }.Select(n => MeetingMessage.CreateParticipant(meeting.ID, n, author.ID,
                MessageAuthorKind.Human, author.DisplayName, "original " + n, Guid.NewGuid(), now)).ToArray();
            foreach (var message in messages) await LegacyMessageSeed.InsertAsync(db, message);
            db.ChangeTracker.Clear(); await db.Database.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(2, (await db.MeetingParticipants.SingleAsync(x => x.ID == left.ID, cancellationToken: TestContext.Current.CancellationToken)).LeftThroughSequence);
            Assert.Equal(2, (await db.Set<MeetingLeaveOperation>().SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).VisibleThroughSequence);
            var projection = await db.Set<ParticipantMessageView>().Where(x => x.ParticipantId == left.ID).OrderBy(x => x.Sequence).ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(new long[] { 1, 2, 3 }, projection.Select(x => x.Sequence));
            Assert.Equal(messages.Select(x => x.ID), projection.Select(x => x.MessageId));
            Assert.All(await db.MeetingMessages.ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken), x => Assert.Equal("public", x.Visibility));
            Assert.Equal(0, await db.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
            db.ChangeTracker.Clear(); await db.Database.MigrateAsync(previous, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(4, await db.Database.SqlQuery<long>($"SELECT LeftThroughSequence AS Value FROM MeetingParticipants WHERE ID = {left.ID}").SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
            await db.Database.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(6, await db.Set<ParticipantMessageView>().CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        }
        finally { File.Delete(path); }
    }
}
