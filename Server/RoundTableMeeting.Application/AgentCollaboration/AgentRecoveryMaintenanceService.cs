using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.Application.AgentCollaboration;

public sealed partial class AgentCollaborationService
{
    public async Task<bool> ReconcileConnectionAsync(AgentConnectionRegistration expected,
        AgentTaskFailureCode? forcedReason = null, CancellationToken cancellationToken = default)
    {
        using var gate = await realtimeGate.AcquireSharedAsync(expected.MeetingId, cancellationToken);
        if (gate is null) return false;
        var participant = (await participants.ListAsync(expected.MeetingId, cancellationToken))
            .SingleOrDefault(item => item.ID == expected.ParticipantId);
        if (participant is null) return false;
        await using var lease = await joinCoordinator.AcquireAsync(expected.MeetingId, participant.NormalizedDisplayName, cancellationToken);
        return await ReconcileOwnedBindingAsync(expected, forcedReason, cancellationToken);
    }

    // Activation compensation already owns the gate and participant lock. Do not acquire
    // either again here; closure/scanning enters through ReconcileConnectionAsync above.
    private async Task<bool> ReconcileOwnedBindingAsync(AgentConnectionRegistration expected,
        AgentTaskFailureCode? forcedReason, CancellationToken cancellationToken)
    {
        var disconnected = false;
        var changed = await transactionRunner.RunAsync(expected.MeetingId, async token =>
        {
            if (await meetings.FindActiveByIdAsync(expected.MeetingId, token) is null) return false;
            var connection = await agentRepository.FindConnectionAsync(expected.MeetingId, expected.ConnectionSessionId, token);
            var cursor = await agentRepository.FindCursorAsync(expected.MeetingId, expected.ParticipantId, token);
            if (connection is null || cursor is null || cursor.CurrentConnectionId != connection.ID
                || cursor.ConnectionGeneration != expected.ConnectionGeneration || Registration(connection) != expected
                || connection.State is not (AgentConnectionState.Activating or AgentConnectionState.Connected)) return false;
            var now = UtcNow();
            var registered = connectionRegistry.IsRegistered(expected.MeetingId, expected.ParticipantId, expected.ConnectionSessionId, expected.TransportBinding);
            var reason = forcedReason;
            if (reason is null && !registered) reason = AgentTaskFailureCode.ServerRestart;
            if (reason is null && connection.ReliabilityMode == AgentReliabilityProtocol.ExplicitAck
                && connection.State == AgentConnectionState.Connected && connection.LeaseExpiresAtUtc <= now)
                reason = AgentTaskFailureCode.ConnectionLeaseExpired;
            if (reason is not null)
            {
                if (!await recoveryRepository.DisconnectAsync(Fence(connection), Domain.AgentCollaboration.AgentTaskFailurePolicy.ToStoredCode(reason.Value), now, token)) return false;
                await InvalidateUnfinishedTasksAsync(connection, reason.Value, now, token);
                RecordVisibleChange(cursor, now);
                disconnected = true;
                return true;
            }
            if (connection.ReliabilityMode != AgentReliabilityProtocol.ExplicitAck) return false;
            var head = (await agentRepository.ListTasksAsync(expected.MeetingId, expected.ParticipantId, token))
                .FirstOrDefault(task => task.State is not (AgentReplyTaskState.Replied or AgentReplyTaskState.Cancelled));
            if (head?.State != AgentReplyTaskState.Delivered || head.CurrentAttemptId is not Guid attemptId) return false;
            var attempt = await agentRepository.FindDeliveryAttemptAsync(expected.MeetingId, head.ID, attemptId, token);
            if (attempt is null || !IsCurrentAttempt(connection, head, attempt) || attempt.ConfirmationDeadlineUtc > now) return false;
            FailCurrentAttempt(head, attempt, cursor, AgentTaskFailureCode.ConfirmationTimeout, now);
            return true;
        }, cancellationToken);
        if (!changed) return false;
        if (disconnected) connectionRegistry.Retire(expected);
        taskSignals.Signal(expected.MeetingId, expected.ParticipantId);
        await PublishActivityAsync(expected.MeetingId, expected.ParticipantId, notifyPresence: disconnected);
        return true;
    }
}
