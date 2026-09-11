using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.Application.AgentCollaboration;

public sealed partial class AgentCollaborationService
{
    public async Task<AgentOperationResult> ConfirmReliableAsync(AgentConfirmContextRequest request,
        McpTransportSessionKey? transportSession, CancellationToken cancellationToken = default)
    {
        var invalid = AgentRecoveryRequestValidator.ValidateConfirmation(request);
        if (invalid is not null) return invalid;
        return await RunReliableAsync(request.ConnectionSessionId, transportSession, async (connection, cursor, token) =>
        {
            var now = UtcNow();
            if (request.Kind == "initial")
            {
                var initial = await agentRepository.FindInitialContextAsync(connection.MeetingId, connection.ParticipantId, connection.ID, token);
                if (initial is null || initial.ID != request.InitialDeliveryId) return ReliableFailure("delivery_attempt_invalid");
                var replayed = initial.State == InitialContextState.Confirmed;
                if (!replayed)
                {
                    ExtendLease(connection, now);
                    initial.Confirm(now);
                    cursor.ConfirmThrough(cursor.FirstJoinSnapshotSequence, now);
                    RecordVisibleChange(cursor, now);
                    unitOfWork.RegisterEdit(initial);
                }
                return AgentOperationResult.Success(new AgentConfirmContextResultDto(replayed, cursor.DeliveredThroughSequence,
                    AgentCollaborationContract.WaitToolName, InitialDeliveryId: initial.ID), "The initial context was confirmed.");
            }
            var task = await agentRepository.FindTaskForAgentAsync(connection.MeetingId, connection.ParticipantId, request.TaskId!.Value, token);
            if (task is null) return ReliableFailure("task_not_available");
            var attempt = await agentRepository.FindDeliveryAttemptAsync(connection.MeetingId, task.ID, request.DeliveryAttemptId!.Value, token);
            if (attempt is null) return ReliableFailure("delivery_attempt_invalid");
            if (!IsCurrentAttempt(connection, task, attempt)) return ReliableFailure("delivery_attempt_stale", "get_agent_status");
            if (attempt.State == AgentTaskDeliveryAttemptState.Confirmed && task.State is AgentReplyTaskState.Processing or AgentReplyTaskState.Replied)
                return AgentOperationResult.Success(new AgentConfirmContextResultDto(true, cursor.DeliveredThroughSequence,
                    task.State == AgentReplyTaskState.Replied ? AgentCollaborationContract.WaitToolName : AgentCollaborationContract.ReplyToolName,
                    TaskId: task.ID, DeliveryAttemptId: attempt.ID), "The context confirmation was replayed.");
            if (task.State != AgentReplyTaskState.Delivered || attempt.State != AgentTaskDeliveryAttemptState.Delivered)
                return ReliableFailure("task_not_available");
            if (attempt.ConfirmationDeadlineUtc is not DateTimeOffset deadline || now >= deadline)
                return ReliableFailure("confirmation_expired", "get_agent_status");
            ExtendLease(connection, now);
            attempt.Confirm(now);
            task.MarkProcessing(now);
            cursor.ConfirmThrough(task.TriggerSequence, now);
            RecordVisibleChange(cursor, now);
            unitOfWork.RegisterEdit(task);
            unitOfWork.RegisterEdit(attempt);
            return AgentOperationResult.Success(new AgentConfirmContextResultDto(false, cursor.DeliveredThroughSequence,
                AgentCollaborationContract.ReplyToolName, TaskId: task.ID, DeliveryAttemptId: attempt.ID), "The task context was confirmed.");
        }, cancellationToken);
    }

    public Task<AgentOperationResult> RenewReliableAsync(AgentLeaseRequest request, McpTransportSessionKey? transportSession,
        CancellationToken cancellationToken = default) => RunReliableAsync(request.ConnectionSessionId, transportSession,
        (connection, _, _) =>
        {
            var now = UtcNow();
            ExtendLease(connection, now);
            return Task.FromResult(AgentOperationResult.Success(new AgentLeaseResultDto(now, connection.LeaseExpiresAtUtc!.Value,
                options.LeaseRenewIntervalSeconds), "The Agent lease was renewed."));
        }, cancellationToken);

    private async Task<AgentOperationResult> RunReliableAsync(Guid? connectionId, McpTransportSessionKey? transportSession,
        Func<AgentConnectionSession, AgentContextCursor, CancellationToken, Task<AgentOperationResult>> action,
        CancellationToken cancellationToken)
    {
        if (!TryResolveRegistration(connectionId, transportSession, out var registration)) return ReliableFailure("connection_invalid");
        using var gate = await realtimeGate.AcquireSharedAsync(registration.MeetingId, cancellationToken);
        if (gate is null) return ReliableFailure("meeting_not_found");
        var agent = (await participants.ListAsync(registration.MeetingId, cancellationToken)).SingleOrDefault(item => item.ID == registration.ParticipantId);
        if (agent is null) return ReliableFailure("meeting_not_found");
        await using var lease = await joinCoordinator.AcquireAsync(registration.MeetingId, agent.NormalizedDisplayName, cancellationToken);
        AgentOperationResult result;
        try
        {
            result = await transactionRunner.RunAsync(registration.MeetingId, async token =>
            {
                if (await meetings.FindActiveByIdAsync(registration.MeetingId, token) is null) return ReliableFailure("meeting_not_found");
                var connection = await agentRepository.FindConnectionAsync(registration.MeetingId, registration.ConnectionSessionId, token);
                if (connection is null) return ReliableFailure("connection_invalid");
                var cursor = await agentRepository.FindCursorAsync(registration.MeetingId, registration.ParticipantId, token);
                var error = ReliableConnectionError(connection, cursor, registration, UtcNow());
                if (error is not null) return error;
                return await action(connection, cursor!, token);
            }, cancellationToken);
        }
        catch (ExpiredLeaseException) { return ReliableFailure("connection_expired", AgentReliabilityProtocol.ResumeTool); }
        if (transactionRunner.ChangedParticipants.Contains(registration.ParticipantId))
            await PublishActivityAsync(registration.MeetingId, registration.ParticipantId);
        return result;
    }

    private AgentOperationResult? ReliableConnectionError(AgentConnectionSession connection, AgentContextCursor? cursor,
        AgentConnectionRegistration registration, DateTimeOffset now)
    {
        if (connection.ParticipantId != registration.ParticipantId || connection.MeetingId != registration.MeetingId)
            return ReliableFailure("connection_invalid");
        if (connection.State == AgentConnectionState.Superseded || cursor?.CurrentConnectionId != connection.ID
            || cursor.ConnectionGeneration != connection.ConnectionGeneration)
            return connection.ReliabilityMode == AgentReliabilityProtocol.Legacy ? Failure("connection_superseded", "The Agent connection was replaced.")
                : ReliableFailure("connection_superseded");
        if (connection.BindingVersion != registration.BindingVersion || connection.ConnectionGeneration != registration.ConnectionGeneration
            || connection.McpTransportBinding != registration.TransportBinding) return ReliableFailure("connection_invalid");
        if (connection.ReliabilityMode != AgentReliabilityProtocol.ExplicitAck)
            return Failure("reliability_mode_required", "This tool requires reliable mode.");
        if (connection.State == AgentConnectionState.Disconnected) return ReliableFailure("connection_disconnected", AgentReliabilityProtocol.ResumeTool);
        if (connection.State != AgentConnectionState.Connected) return ReliableFailure("connection_invalid");
        if (connection.LeaseExpiresAtUtc is not DateTimeOffset expiry || now >= expiry)
            return ReliableFailure("connection_expired", AgentReliabilityProtocol.ResumeTool);
        return connectionRegistry.IsRegistered(registration.MeetingId, registration.ParticipantId, registration.ConnectionSessionId, registration.TransportBinding)
            ? null : ReliableFailure("connection_invalid");
    }

    private void ExtendLease(AgentConnectionSession connection, DateTimeOffset now, int? waitSeconds = null)
    {
        if (!connection.Renew(connection.BindingVersion, now, options.ConnectionLeaseSeconds, waitSeconds, options.LeaseSafetyMarginSeconds))
            throw new ExpiredLeaseException();
        unitOfWork.RegisterEdit(connection);
    }

    private void RecordVisibleChange(AgentContextCursor cursor, DateTimeOffset now)
    {
        // Revision is assigned after comparing the final visible payload in AgentActivityTransactions.
        unitOfWork.RegisterEdit(cursor);
    }

    private static bool IsCurrentAttempt(AgentConnectionSession connection, AgentReplyTask task, AgentTaskDeliveryAttempt attempt) =>
        task.CurrentAttemptId == attempt.ID && attempt.ConnectionSessionId == connection.ID
        && attempt.ConnectionGeneration == connection.ConnectionGeneration && attempt.BindingVersion == connection.BindingVersion
        && attempt.RetryCycle == task.RetryCycle && attempt.InvalidatedAtUtc is null && attempt.State != AgentTaskDeliveryAttemptState.Abandoned;

    private sealed class ExpiredLeaseException : Exception;
}
