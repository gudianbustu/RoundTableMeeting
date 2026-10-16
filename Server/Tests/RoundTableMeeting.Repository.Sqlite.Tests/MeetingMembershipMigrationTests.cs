using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;
namespace RoundTableMeeting.Repository.Sqlite.Tests;

public sealed class MeetingMembershipMigrationTests
{
    private const string Previous = "20260911080309_AddMultiAgentCollaboration";

    [Fact]
    public async Task DowngradeRefusesToDiscardDurableLifecycleProof()
    {
        var path = Path.Combine(Path.GetTempPath(), "meeting-membership-guard-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await using var db = new RoundTableMeetingDbContext(new DbContextOptionsBuilder<RoundTableMeetingDbContext>()
                .UseSqlite($"Data Source={path};Foreign Keys=True;Pooling=False").Options);
            await db.Database.MigrateAsync();
            var proof = MeetingLifecycleProof.Create(Guid.NewGuid(), new string('A', 64));
            db.Add(proof); await db.SaveChangesAsync();
            var error = await Assert.ThrowsAsync<SqliteException>(() => db.Database.MigrateAsync(Previous));
            Assert.Contains("CK_PreserveLifecycleFacts", error.Message);
            Assert.True(await db.Set<MeetingLifecycleProof>().AnyAsync(p => p.ID == proof.ID));
            Assert.Contains("20261015131414_AddMeetingMembershipLifecycle", await db.Database.GetAppliedMigrationsAsync());
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpgradePreservesOldMeetingStateAndHistoryAndMakesDeletedReceiptsPermanent(bool deleting)
    {
        var path = Path.Combine(Path.GetTempPath(), "meeting-membership-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await using var db = new RoundTableMeetingDbContext(new DbContextOptionsBuilder<RoundTableMeetingDbContext>()
                .UseSqlite($"Data Source={path};Foreign Keys=True;Pooling=False").Options);
            await db.Database.MigrateAsync(Previous);
            var now = DateTimeOffset.UtcNow;
            var user = UserAccount.Create("migration", "Migration"); user.InitializePasswordHash("test-hash");
            var meeting = Meeting.Create("Keep current intent", "Existing background", user.ID, now);
            if (deleting) meeting.BeginDeleting();
            db.AddRange(user, meeting, AgentCollaborationBoundary.Create(meeting.ID)); await db.SaveChangesAsync();
            var participant = MeetingParticipant.CreateHuman(meeting.ID, user.ID, "Migration", now);
            await LegacyMeetingParticipantSeed.InsertAsync(db, participant);
            var message = MeetingMessage.CreateParticipant(meeting.ID, 1, participant.ID, MessageAuthorKind.Human, participant.DisplayName, "Exact old history", Guid.NewGuid(), now);
            var receipt = MeetingCreationReceipt.Create(user.ID, Guid.NewGuid(), Guid.NewGuid(), now);
            receipt.MarkDeleted(now);
            await LegacyMessageSeed.InsertAsync(db, message);
            db.Add(receipt); await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE MeetingCreationReceipts SET ExpiresAtUtc = {now.AddDays(-1)} WHERE ID = {receipt.ID}");
            db.ChangeTracker.Clear();
            await db.Database.MigrateAsync();
            var migrated = await db.MeetingParticipants.SingleAsync();
            Assert.Equal(MeetingMembershipState.Joined, migrated.MembershipState);
            Assert.Equal(1, migrated.MembershipRevision);
            Assert.Null(migrated.LeftThroughSequence);
            Assert.Equal(participant.ID, migrated.ID);
            Assert.Equal(deleting ? MeetingLifecycleState.Deleting : MeetingLifecycleState.Active, (await db.Meetings.SingleAsync()).LifecycleState);
            Assert.Equal("Exact old history", (await db.MeetingMessages.SingleAsync()).Content);
            Assert.Null((await db.MeetingCreationReceipts.SingleAsync()).ExpiresAtUtc);
            Assert.Equal(0, await db.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync());
        }
        finally { File.Delete(path); }
    }
}
