using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;
using RoundTableMeeting.Repository.Sqlite.Services;

namespace RoundTableMeeting.Repository.Sqlite.Tests;

public sealed class RealtimeChatDeliveryPersistenceTests
{
    [Fact]
    public async Task DeliveryStateTransitionsPersistWithNoTrackingDefault()
    {
        var root = Path.Combine(Path.GetTempPath(), "RoundTableMeetingDeliveryTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "delivery.db");
        try
        {
            await using (var context = CreateContext(path))
            {
                await context.Database.MigrateAsync();
                var now = DateTimeOffset.UtcNow;
                var user = UserAccount.Create("delivery-owner", "Owner"); user.InitializePasswordHash("hash");
                var meeting = Meeting.Create("Delivery", null, user.ID, now);
                var message = MeetingMessage.CreateSystem(meeting.ID, 1, "System", "test", Guid.NewGuid(), now);
                var appendedEvent = MessageAppendedEvent.Create(meeting.ID, message.ID, message.Sequence, now);
                var delivery = MessageEventDelivery.Create(appendedEvent.ID, "realtime-broadcast-v1", now);
                context.AddRange(user, meeting, message, appendedEvent, delivery);
                await context.SaveChangesAsync();
                context.ChangeTracker.Clear();
                delivery = await context.MessageEventDeliveries.AsTracking().SingleAsync();
                var lease = Guid.NewGuid();
                delivery.Lease(lease, now.AddMinutes(1));
                await context.SaveChangesAsync();
                context.ChangeTracker.Clear();
                delivery = await context.MessageEventDeliveries.AsTracking().SingleAsync();
                delivery.Complete(lease, now.AddSeconds(1));
                await context.SaveChangesAsync();
            }

            await using var verify = CreateContext(path);
            var saved = await verify.MessageEventDeliveries.SingleAsync();
            Assert.Equal(MessageEventDeliveryState.Completed, saved.State);
            Assert.NotNull(saved.CompletedAtUtc);
            Assert.Null(saved.LeaseId);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RecoveryServicePersistsSuspendedDeliveryResumeWithNoTrackingDefault()
    {
        var root = Path.Combine(Path.GetTempPath(), "RoundTableMeetingDeliveryTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "resume.db");
        try
        {
            Guid deliveryId;
            var now = DateTimeOffset.UtcNow;
            await using (var context = CreateContext(path))
            {
                await context.Database.MigrateAsync();
                var user = UserAccount.Create("resume-owner", "Owner"); user.InitializePasswordHash("hash");
                var meeting = Meeting.Create("Resume", null, user.ID, now);
                var message = MeetingMessage.CreateSystem(meeting.ID, 1, "System", "test", Guid.NewGuid(), now);
                var appendedEvent = MessageAppendedEvent.Create(meeting.ID, message.ID, message.Sequence, now);
                var delivery = MessageEventDelivery.Create(appendedEvent.ID, "realtime-broadcast-v1", now);
                var lease = Guid.NewGuid();
                delivery.Lease(lease, now.AddMinutes(1));
                delivery.Fail(lease, now.AddMinutes(2), true);
                deliveryId = delivery.ID;
                context.AddRange(user, meeting, message, appendedEvent, delivery);
                await context.SaveChangesAsync();
            }

            await using (var context = CreateContext(path))
            {
                var service = new MessageEventRecoveryService(context, TimeProvider.System);
                Assert.True(await service.ResumeAsync(deliveryId));
            }

            await using var verify = CreateContext(path);
            Assert.Equal(MessageEventDeliveryState.Pending, (await verify.MessageEventDeliveries.SingleAsync()).State);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task MessageAuthorChecksRejectInvalidPersistedValues()
    {
        var root = Path.Combine(Path.GetTempPath(), "RoundTableMeetingDeliveryTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "checks.db");
        try
        {
            await using var context = CreateContext(path);
            await context.Database.MigrateAsync();
            await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlRawAsync("INSERT INTO MeetingMessages (ID, MeetingId, Sequence, AuthorParticipantId, AuthorKind, AuthorDisplayName, Content, ClientSubmissionId, CreatedAtUtc) VALUES ({0}, {1}, 1, NULL, 'Invalid', 'x', 'x', {2}, {3})", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static RoundTableMeetingDbContext CreateContext(string path)
        => new(new DbContextOptionsBuilder<RoundTableMeetingDbContext>()
            .UseSqlite($"Data Source={path};Foreign Keys=True;Pooling=False")
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options);
}
