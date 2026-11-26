using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Hosting;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Fact]
    public async Task LocalMaintenanceCommandUsesProductionDiAndPreservesSuspendedEventIdentity()
    {
        using var factory = new TestApplicationFactory();
        using var browser = CreateBrowserClient(factory);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Local event maintenance");
        await PostHumanMessageAsync(browser, meeting.MeetingId, "maintenance fixture");
        Guid deliveryId;
        Guid eventId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            eventId = await db.MessageAppendedEvents.Where(x => x.MeetingId == meeting.MeetingId).Select(x => x.ID).SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
            var now = DateTimeOffset.UtcNow;
            var delivery = MessageEventDelivery.Create(eventId, "isolated-maintenance-consumer", now);
            var lease = Guid.NewGuid();
            delivery.Lease(lease, now.AddMinutes(1));
            Assert.True(delivery.Fail(lease, now, true));
            db.Add(delivery);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            deliveryId = delivery.ID;
        }
        Assert.False(await MessageEventMaintenance.TryRunAsync([], factory.Services));
        foreach (var invalid in new[] { "not-a-guid", Guid.Empty.ToString() })
            await Assert.ThrowsAsync<ArgumentException>(() => MessageEventMaintenance.TryRunAsync([$"--resume-message-event={invalid}"], factory.Services));
        var command = $"--resume-message-event={deliveryId}";
        await Assert.ThrowsAsync<ArgumentException>(() => MessageEventMaintenance.TryRunAsync([command, command], factory.Services));
        Assert.True(await MessageEventMaintenance.TryRunAsync([command], factory.Services));
        await Assert.ThrowsAsync<InvalidOperationException>(() => MessageEventMaintenance.TryRunAsync([command], factory.Services));
        await using var check = factory.Services.CreateAsyncScope();
        var database = check.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        var saved = await database.MessageEventDeliveries.SingleAsync(x => x.ID == deliveryId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(eventId, saved.EventId);
        Assert.Equal(MessageEventDeliveryState.Pending, saved.State);
        Assert.Equal(1, saved.AttemptCount);
        Assert.Equal(1, await database.MeetingMessages.CountAsync(x => x.MeetingId == meeting.MeetingId, cancellationToken: TestContext.Current.CancellationToken));
    }
}
