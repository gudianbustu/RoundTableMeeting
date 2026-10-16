using System.Security.Cryptography;
using System.Text;
using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.Application.AgentCollaboration;

public sealed partial class AgentCollaborationService
{
    public async Task<AgentOperationResult> ResumeReliableAsync(AgentResumeRequest request, McpTransportSessionKey? transportSession,
        string? sourceAddress = null, CancellationToken cancellationToken = default)
    {
        var invalid = AgentRecoveryRequestValidator.ValidateResume(request);
        if (invalid is not null) return invalid;
        if (transportSession is not { IsEmpty: false } session) return ReliableFailure("connection_invalid");
        await using var reservation = await connectionRegistry.ReserveTransportAsync(session, cancellationToken);
        // A transport already bound to this legacy identity can receive a mode error.
        // Reuse the durable ownership fence; an anonymous recovery attempt still follows
        // the uniform key-rejection path below and cannot probe the target's mode.
        if (connectionRegistry.TryGetRegistration(session, out var bound)
            && bound.ConnectionSessionId == request.ConnectionSessionId
            && bound.ReliabilityMode == AgentReliabilityProtocol.Legacy)
            return await RunReliableAsync(request.ConnectionSessionId, session,
                (_, _, _) => Task.FromResult(Failure("reliability_mode_required", "This tool requires reliable mode.")), cancellationToken);
        if (!resumeRateLimiter.TryAcquire(sourceAddress, request.ConnectionSessionId!.Value, out var admission, out var retryAfter))
            return AgentOperationResult.Failure("agent_operation_unavailable", "Recovery is temporarily unavailable.",
                new AgentRecoveryErrorData("stop") { RetryAfterSeconds = retryAfter });
        var preflight = await recoveryRepository.FindConnectionAsync(request.ConnectionSessionId.Value, cancellationToken);
        if (!AgentRecoveryCredentials.VerifyKey(request.RecoveryKey, preflight?.RecoveryKeyHash))
        {
            admission!.RecordFailure();
            return ReliableFailure("resume_rejected");
        }
        using var gate = await realtimeGate.AcquireSharedAsync(preflight!.MeetingId, cancellationToken);
        if (gate is null) return ReliableFailure("meeting_not_found");
        var participant = (await participants.ListAsync(preflight.MeetingId, cancellationToken)).SingleOrDefault(item => item.ID == preflight.ParticipantId);
        if (participant is null) return ReliableFailure("meeting_not_found");
        if (participant.MembershipState == MeetingMembershipState.Left) return ReliableFailure("membership_left");
        await using var participantLease = await joinCoordinator.AcquireAsync(preflight.MeetingId, participant.NormalizedDisplayName, cancellationToken);
        var binding = session.ToStorageBinding();
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(binding)));
        var retire = new List<AgentConnectionRegistration>();
        var outcome = await transactionRunner.RunAsync(preflight.MeetingId, async token =>
        {
            if (await meetings.FindActiveByIdAsync(preflight.MeetingId, token) is null) return ResumeOutcome.Fail("meeting_not_found");
            var connection = await agentRepository.FindConnectionAsync(preflight.MeetingId, preflight.ID, token);
            if (connection is null || !AgentRecoveryCredentials.VerifyKey(request.RecoveryKey, connection.RecoveryKeyHash))
                return ResumeOutcome.Fail("resume_rejected");
            var cursor = await agentRepository.FindCursorAsync(connection.MeetingId, connection.ParticipantId, token);
            if (cursor is null || cursor.CurrentConnectionId != connection.ID || cursor.ConnectionGeneration != connection.ConnectionGeneration
                || connection.State == AgentConnectionState.Superseded) return ResumeOutcome.Fail("connection_superseded");
            var receipt = await recoveryRepository.FindResumeOperationAsync(connection.MeetingId, connection.ID, request.OperationId!.Value, token);
            if (receipt is not null)
            {
                if (receipt.RequestFingerprint != fingerprint || receipt.TargetTransportBinding != binding)
                    return ResumeOutcome.Fail("operation_conflict");
                if (receipt.ResultBindingVersion != connection.BindingVersion || connection.State == AgentConnectionState.Disconnected)
                    return ResumeOutcome.Fail("operation_superseded");
                if (connection.State == AgentConnectionState.Connected && (connection.LeaseExpiresAtUtc <= UtcNow()
                    || !connectionRegistry.IsRegistered(connection.MeetingId, connection.ParticipantId, connection.ID, binding)))
                {
                    await DisconnectForRecoveryAsync(connection, AgentTaskFailureCode.ConnectionLeaseExpired, retire, token);
                    return ResumeOutcome.Fail("operation_superseded");
                }
                return new ResumeOutcome(null, connection, true, connection.State == AgentConnectionState.Activating);
            }
            // A failed activation can leave no in-memory registration. The durable binding
            // still forbids starting a new resume operation on that same MCP transport.
            if (connection.McpTransportBinding == binding || connectionRegistry.ContainsTransport(session))
                return ResumeOutcome.Fail("connection_invalid");
            if (connection.State == AgentConnectionState.Activating) return ResumeOutcome.Fail("operation_pending");
            var now = UtcNow();
            var oldFence = Fence(connection);
            if (!await recoveryRepository.RebindAsync(oldFence, binding, now, token)) return ResumeOutcome.Fail("agent_operation_conflict");
            retire.AddRange(connectionRegistry.ListRegistrations(connection.MeetingId).Where(item =>
                item.ConnectionSessionId == connection.ID && item.BindingVersion == oldFence.BindingVersion));
            if (!connection.BeginResume(oldFence.BindingVersion, binding, now)) throw new InvalidOperationException("The resumed state diverged from its conditional update.");
            await InvalidateUnfinishedTasksAsync(connection, AgentTaskFailureCode.ConnectionResumed, now, token);
            RecordVisibleChange(cursor, now);
            unitOfWork.RegisterAdd(AgentRecoveryOperation.ForResume(connection.MeetingId, connection.ParticipantId, connection.ID,
                request.OperationId.Value, fingerprint, connection.ConnectionGeneration, connection.BindingVersion, binding, now));
            return new ResumeOutcome(null, connection, false, true);
        }, cancellationToken);
        foreach (var previous in retire) connectionRegistry.Retire(previous);
        await PublishChangedActivitiesAsync(preflight.MeetingId);
        if (outcome.Error is not null) return outcome.Error;
        if (outcome.NeedsActivation && !await FinalizeReliableActivationAsync(outcome.Connection!, session, cancellationToken))
            return ReliableFailure("agent_operation_unavailable");
        var registration = Registration(outcome.Connection!);
        var current = await agentRepository.FindConnectionAsync(registration.MeetingId, registration.ConnectionSessionId, cancellationToken);
        var currentCursor = await agentRepository.FindCursorAsync(registration.MeetingId, registration.ParticipantId, cancellationToken);
        if (current is null) return ReliableFailure("meeting_not_found");
        var error = ReliableConnectionError(current, currentCursor, registration, UtcNow());
        if (error is not null) return error;
        var initial = await agentRepository.FindInitialContextAsync(current.MeetingId, current.ParticipantId, current.ID, cancellationToken);
        if (initial is null) return ReliableFailure("agent_operation_unavailable");
        var pendingInitial = initial.State == InitialContextState.Prepared;
        var context = pendingInitial ? await contextBuilder.RebuildFrozenAsync(current.MeetingId, initial.GetWindow(), cancellationToken) : null;
        var tasks = await agentRepository.ListTasksAsync(current.MeetingId, current.ParticipantId, cancellationToken);
        var head = tasks.FirstOrDefault(task => task.State is not (AgentReplyTaskState.Replied or AgentReplyTaskState.Cancelled));
        var replied = tasks.LastOrDefault(task => task.State == AgentReplyTaskState.Replied);
        var headSummary = head is null ? null : await ReliableSummaryAsync(head, cancellationToken);
        var lastReply = replied is null ? null : await ReliableSummaryAsync(replied, cancellationToken);
        var next = pendingInitial ? AgentReliabilityProtocol.ConfirmTool
            : head is { RetryExhausted: true } ? "get_agent_status" : AgentCollaborationContract.WaitToolName;
        if (!outcome.Replayed) await PublishActivityAsync(current.MeetingId, current.ParticipantId, notifyPresence: true);
        var finalError = ReliableConnectionError(current, currentCursor, registration, UtcNow());
        if (finalError is not null) return finalError;
        return AgentOperationResult.Success(new AgentResumeResultDto(ReliableConnectionDto(current, initial), outcome.Replayed,
            pendingInitial ? initial.ID : null, context, headSummary, lastReply, options.ToLeasePolicy(), AgentReliabilityProtocol.Capabilities, next),
            "The Agent connection was resumed.");
    }

    private async Task<bool> FinalizeReliableActivationAsync(AgentConnectionSession connection, McpTransportSessionKey session,
        CancellationToken cancellationToken)
    {
        var registration = Registration(connection);
        RetireOlderRegistrations(registration);
        var activated = false;
        try
        {
            if (connectionRegistry.TryActivate(session, registration, null))
                activated = await transactionRunner.RunAsync(registration.MeetingId, async token =>
                {
                    if (!connectionRegistry.IsRegistered(registration.MeetingId, registration.ParticipantId, registration.ConnectionSessionId, registration.TransportBinding)) return false;
                    var now = UtcNow();
                    if (!await recoveryRepository.ActivateAsync(ToFence(registration), now, options.ConnectionLeaseSeconds, token)) return false;
                    var cursor = await agentRepository.FindCursorAsync(registration.MeetingId, registration.ParticipantId, token);
                    RecordVisibleChange(cursor!, now);
                    return true;
                }, cancellationToken);
            activated = activated && connectionRegistry.IsRegistered(registration.MeetingId, registration.ParticipantId, registration.ConnectionSessionId, registration.TransportBinding);
            return activated;
        }
        finally
        {
            if (!activated)
            {
                connectionRegistry.Retire(registration);
                await ReconcileOwnedBindingAsync(registration, AgentTaskFailureCode.TransportClosed, CancellationToken.None);
            }
        }
    }

    private async Task<AgentTaskSummaryDto> ReliableSummaryAsync(AgentReplyTask task, CancellationToken cancellationToken)
    {
        var attempt = task.CurrentAttemptId is Guid attemptId
            ? await agentRepository.FindDeliveryAttemptAsync(task.MeetingId, task.ID, attemptId, cancellationToken) : null;
        var reason = task.State switch
        {
            AgentReplyTaskState.Delivered => attempt?.ConfirmationDeadlineUtc <= UtcNow() ? "confirmation_overdue" : "awaiting_context_confirmation",
            AgentReplyTaskState.Processing => "processing",
            AgentReplyTaskState.Failed => task.RetryExhausted ? "retry_exhausted" : "retry_pending",
            AgentReplyTaskState.Replied => "replied",
            AgentReplyTaskState.Cancelled => "cancelled",
            _ => "waiting"
        };
        return new(task.ID, task.TriggerSequence, ToWire(task.State), task.FailureCode)
        {
            Collaboration = CollaborationFor(task),
            Reason = reason, CurrentAttemptId = task.CurrentAttemptId, ConfirmationDeadline = attempt?.ConfirmationDeadlineUtc,
            RetryCycle = task.RetryCycle, AutomaticRetryCount = task.AutomaticRetryCount, RetryExhausted = task.RetryExhausted,
            StopReason = RoundTableMeeting.Domain.AgentCollaboration.AgentTaskFailurePolicy.ToWire(task.StopReason),
            NextRetryAt = task.NextRetryAtUtc, ReplyMessageId = task.ReplyMessageId,
            NextAction = task.State == AgentReplyTaskState.Processing ? "continue_processing"
                : task.RetryExhausted ? "await_explicit_retry" : AgentCollaborationContract.WaitToolName
        };
    }

    private sealed record ResumeOutcome(AgentOperationResult? Error, AgentConnectionSession? Connection, bool Replayed, bool NeedsActivation)
    {
        public static ResumeOutcome Fail(string code) => new(ReliableFailure(code), null, false, false);
    }
}
