using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.Domain.Entities;

public sealed class MessageEventDelivery : BaseEntity
{
    public Guid EventId { get; private set; }
    public string ConsumerName { get; private set; } = string.Empty;
    public MessageEventDeliveryState State { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTimeOffset AvailableAtUtc { get; private set; }
    public Guid? LeaseId { get; private set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public MessageEventDelivery() { }
    private MessageEventDelivery(Guid eventId, string consumerName, DateTimeOffset availableAtUtc)
    {
        if (eventId == Guid.Empty) throw new ArgumentException("Event is required.", nameof(eventId));
        if (string.IsNullOrWhiteSpace(consumerName)) throw new ArgumentException("Consumer is required.", nameof(consumerName));
        EventId = eventId; ConsumerName = consumerName; AvailableAtUtc = availableAtUtc; State = MessageEventDeliveryState.Pending;
    }
    public static MessageEventDelivery Create(Guid eventId, string consumerName, DateTimeOffset availableAtUtc) => new(eventId, consumerName, availableAtUtc);
    public void Lease(Guid leaseId, DateTimeOffset expiresAtUtc)
    {
        if (leaseId == Guid.Empty) throw new ArgumentException("Lease is required.", nameof(leaseId));
        if (expiresAtUtc <= AvailableAtUtc) throw new ArgumentOutOfRangeException(nameof(expiresAtUtc));
        if (State is not MessageEventDeliveryState.Pending and not MessageEventDeliveryState.Leased) throw new InvalidOperationException("Delivery is not available.");
        State = MessageEventDeliveryState.Leased; LeaseId = leaseId; LeaseExpiresAtUtc = expiresAtUtc;
    }
    public bool Complete(Guid leaseId, DateTimeOffset completedAtUtc)
    {
        if (State != MessageEventDeliveryState.Leased || LeaseId != leaseId) return false;
        State = MessageEventDeliveryState.Completed; CompletedAtUtc = completedAtUtc; LeaseId = null; LeaseExpiresAtUtc = null; return true;
    }
    public bool Fail(Guid leaseId, DateTimeOffset availableAtUtc, bool suspend)
    {
        if (State != MessageEventDeliveryState.Leased || LeaseId != leaseId) return false;
        AttemptCount++; State = suspend ? MessageEventDeliveryState.Suspended : MessageEventDeliveryState.Pending; AvailableAtUtc = availableAtUtc; LeaseId = null; LeaseExpiresAtUtc = null; return true;
    }
    public void Resume(DateTimeOffset availableAtUtc)
    {
        if (State != MessageEventDeliveryState.Suspended) throw new InvalidOperationException("Delivery is not suspended.");
        State = MessageEventDeliveryState.Pending; AvailableAtUtc = availableAtUtc; CompletedAtUtc = null; LeaseId = null; LeaseExpiresAtUtc = null;
    }
}
