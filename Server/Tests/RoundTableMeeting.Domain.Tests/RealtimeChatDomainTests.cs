using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.Domain.Tests;

public sealed class RealtimeChatDomainTests
{
    [Fact]
    public void DeliveryRejectsStaleLeaseCompletionAndSuspendsAtCallerDecision()
    {
        var now = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        var delivery = MessageEventDelivery.Create(Guid.NewGuid(), "realtime-broadcast-v1", now);
        var firstLease = Guid.NewGuid();
        var secondLease = Guid.NewGuid();

        delivery.Lease(firstLease, now.AddSeconds(30));
        delivery.Lease(secondLease, now.AddSeconds(60));

        Assert.False(delivery.Complete(firstLease, now.AddSeconds(1)));
        Assert.False(delivery.Fail(firstLease, now.AddSeconds(2), false));
        Assert.True(delivery.Fail(secondLease, now.AddSeconds(2), true));
        Assert.Equal(MessageEventDeliveryState.Suspended, delivery.State);
        Assert.Equal(1, delivery.AttemptCount);

        delivery.Resume(now.AddSeconds(3));
        Assert.Equal(MessageEventDeliveryState.Pending, delivery.State);
        Assert.Null(delivery.LeaseId);
    }
}
