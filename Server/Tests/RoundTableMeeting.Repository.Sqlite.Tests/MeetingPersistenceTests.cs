using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Repository.Sqlite.Database;
using RoundTableMeeting.Repository.Sqlite.Transactions;
using RoundTableMeeting.Repository.Sqlite.UnitOfWork;

namespace RoundTableMeeting.Repository.Sqlite.Tests;

public sealed class MeetingPersistenceTests
{
    [Fact]
    public async Task MeetingCodeAndParticipantNameUniqueIndexesAreEnforced()
    {
        var (root, context) = await CreateContextAsync();
        try
        {
            var user = UserAccount.Create("meeting-owner", "Owner"); user.InitializePasswordHash("hash");
            var meeting = Meeting.Create("One", null, user.ID, DateTimeOffset.UtcNow);
            context.Add(user); context.Add(meeting); context.Add(MeetingParticipant.CreateHuman(meeting.ID, user.ID, "Owner", DateTimeOffset.UtcNow));
            await context.SaveChangesAsync();
            var duplicate = Meeting.Create("Two", null, user.ID, DateTimeOffset.UtcNow);
            context.Entry(duplicate).Property(nameof(Meeting.MeetingCode)).CurrentValue = meeting.MeetingCode;
            context.Add(duplicate);
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }
        finally { await context.DisposeAsync(); SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MeetingTransactionRunnerCommitsAndRejectsNestedTransactions()
    {
        var (root, context) = await CreateContextAsync();
        try
        {
            var unitOfWork = new UnitOfWorkImpl(context);
            var runner = new MeetingTransactionRunner(context, unitOfWork);
            var user = UserAccount.Create("runner-owner", "Owner"); user.InitializePasswordHash("hash");
            await runner.RunAsync(token => { unitOfWork.RegisterAdd(user); return Task.FromResult(true); });
            Assert.Equal(1, await context.UserAccounts.CountAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(_ => runner.RunAsync(__ => Task.FromResult(true))));
        }
        finally { await context.DisposeAsync(); SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private static async Task<(string Root, RoundTableMeetingDbContext Context)> CreateContextAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "RoundTableMeetingMeetingTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var context = new RoundTableMeetingDbContext(new DbContextOptionsBuilder<RoundTableMeetingDbContext>()
            .UseSqlite($"Data Source={Path.Combine(root, "meetings.db")};Foreign Keys=True;Pooling=False")
            .Options);
        await context.Database.MigrateAsync();
        return (root, context);
    }
}
