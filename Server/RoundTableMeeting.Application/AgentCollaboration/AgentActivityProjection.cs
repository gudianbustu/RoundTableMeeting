using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.Application.AgentCollaboration;

// This payload has no clock or registry dependency. Equal revisions must project equal
// visible facts, even when the read happens after a lease deadline or a registry loss.
internal sealed record AgentActivityPayload(string State, Guid? ActiveTaskId, string? FailureCode,
    string Reason, bool? RetryExhausted, DateTimeOffset? NextRetryAt, string? ConnectionDisposition)
{
    public static AgentActivityPayload Build(AgentConnectionSession? connection, AgentInitialContextDelivery? initial,
        IReadOnlyList<AgentReplyTask> tasks)
    {
        var disposition = connection is null ? null : connection.BindingVersion > 1 ? "resumed"
            : connection.ConnectionGeneration > 1 ? "taken_over" : "created";
        if (connection is null || connection.State is AgentConnectionState.Disconnected or AgentConnectionState.Superseded)
            return new("disconnected", null, null, connection?.DisconnectReason ?? "connection_disconnected", null, null, disposition);
        if (connection.State == AgentConnectionState.Activating)
            return new("blocked", null, null, "activating", null, null, disposition);
        if (connection.Transport == AgentConnectionSession.ParticipantSequenceTransport)
            return new("waiting", null, null, "waiting", null, null, disposition);
        var reliable = connection.ReliabilityMode == "explicit_ack_v1";
        if (reliable && initial?.State != InitialContextState.Confirmed)
            return new("blocked", null, null, "initial_context_unconfirmed", null, null, disposition);
        var head = tasks.FirstOrDefault(task => task.State is not (AgentReplyTaskState.Replied or AgentReplyTaskState.Cancelled));
        return head?.State switch
        {
            AgentReplyTaskState.Processing => new("processing", head.ID, null, "processing", null, null, disposition),
            AgentReplyTaskState.Delivered => new("blocked", head.ID, reliable ? null : "delivery_not_finalized",
                reliable ? "awaiting_context_confirmation" : "delivery_not_finalized", null, null, disposition),
            AgentReplyTaskState.Failed => new("blocked", head.ID, head.FailureCode,
                reliable ? head.RetryExhausted ? "retry_exhausted" : "retry_pending" : "task_failed",
                reliable ? head.RetryExhausted : null, reliable ? head.NextRetryAtUtc : null, disposition),
            _ => new("waiting", null, null, "waiting", null, null, disposition)
        };
    }

    public AgentActivityDto ToDto(AgentContextCursor cursor) => new(cursor.MeetingId, cursor.ParticipantId, State,
        ActiveTaskId, FailureCode, cursor.ActivityChangedAtUtc)
    { Revision = cursor.ActivityRevision, Reason = Reason, RetryExhausted = RetryExhausted, NextRetryAt = NextRetryAt, ConnectionDisposition = ConnectionDisposition };
}
