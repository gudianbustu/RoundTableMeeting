using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.AgentCollaboration;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.Application.AgentCollaboration;

public sealed partial class AgentCollaborationService
{
    public async Task<AgentOperationResult> JoinReliableAsync(AgentJoinRequest request,
        McpTransportSessionKey? transportSession, CancellationToken cancellationToken = default)
    {
        var validation = AgentRecoveryRequestValidator.ValidateJoin(request);
        if (validation is not null) return validation;
        if (request.ReliabilityMode != AgentReliabilityProtocol.ExplicitAck)
            return ReliableFailure("reliability_mode_required");
        if (transportSession is not { IsEmpty: false } session) return ReliableFailure("connection_invalid");
        await using var reservation = await connectionRegistry.ReserveTransportAsync(session, cancellationToken);
        if (!joinRateLimiter.TryAcquire(request.SourceAddress, request.MeetingCode, request.DisplayName, session, out var retryAfter))
            return AgentOperationResult.Failure("agent_operation_unavailable", "The Agent join is temporarily unavailable.",
                new AgentRecoveryErrorData("stop") { RetryAfterSeconds = retryAfter });
        if (!TryParseMeetingCode(request.MeetingCode, out var meetingCode)) return ReliableFailure("meeting_not_found");
        if (string.IsNullOrWhiteSpace(request.DisplayName)) return ReliableFailure("agent_name_required");
        string displayName;
        try { displayName = MeetingParticipant.NormalizeDisplayName(request.DisplayName); }
        catch (ArgumentException) { return ReliableFailure("agent_name_invalid"); }
        if (!IsContextLimitValid(request.ContextCharacterLimit)) return ReliableFailure("context_limit_invalid");
        var preflight = await meetings.FindActiveByCodeAsync(meetingCode, cancellationToken);
        if (preflight is null) return ReliableFailure("meeting_not_found");
        using var gate = await realtimeGate.AcquireSharedAsync(preflight.ID, cancellationToken);
        if (gate is null) return ReliableFailure("meeting_not_found");
        var name = displayName.ToUpperInvariant();
        await using var participantLease = await joinCoordinator.AcquireAsync(preflight.ID, name, cancellationToken);
        var binding = session.ToStorageBinding();
        var fingerprint = AgentRecoveryCredentials.JoinFingerprint(request);
        var retire = new List<AgentConnectionRegistration>();
        var outcome = await transactionRunner.RunAsync(preflight.ID, async token =>
        {
            var meeting = await meetings.FindActiveByCodeAsync(meetingCode, token);
            if (meeting is null || meeting.ID != preflight.ID) return ReliableJoinOutcome.Fail("meeting_not_found");
            var receipt = await recoveryRepository.FindJoinOperationAsync(meeting.ID, name, request.OperationId!.Value, token);
            if (receipt is not null)
            {
                if (receipt.RequestFingerprint != fingerprint) return ReliableJoinOutcome.Fail("operation_conflict");
                var resultConnection = await agentRepository.FindConnectionAsync(meeting.ID, receipt.ResultConnectionId!.Value, token);
                if (resultConnection is null || !AgentRecoveryCredentials.VerifyKey(request.RecoveryKey, resultConnection.RecoveryKeyHash))
                    return ReliableJoinOutcome.Fail("operation_conflict");
                var cursor = await agentRepository.FindCursorAsync(meeting.ID, receipt.ParticipantId, token);
                if (cursor is null || cursor.CurrentConnectionId != resultConnection.ID
                    || cursor.ConnectionGeneration != resultConnection.ConnectionGeneration || resultConnection.State == AgentConnectionState.Superseded)
                    return ReliableJoinOutcome.Fail("connection_superseded");
                if (receipt.ResultBindingVersion != resultConnection.BindingVersion || receipt.TargetTransportBinding != binding
                    || resultConnection.State == AgentConnectionState.Disconnected)
                    return ReliableJoinOutcome.Recovery(resultConnection.ID);
                var registered = connectionRegistry.IsRegistered(meeting.ID, receipt.ParticipantId, resultConnection.ID, binding);
                if (resultConnection.State == AgentConnectionState.Connected
                    && (!registered || resultConnection.LeaseExpiresAtUtc <= UtcNow()))
                {
                    await DisconnectForRecoveryAsync(resultConnection, AgentTaskFailureCode.ConnectionLeaseExpired, retire, token);
                    return ReliableJoinOutcome.Recovery(resultConnection.ID);
                }
                var agent = await participants.FindByNormalizedNameAsync(meeting.ID, name, token);
                var initial = await agentRepository.FindInitialContextAsync(meeting.ID, receipt.ParticipantId, resultConnection.ID, token);
                if (agent is null || initial is null) return ReliableJoinOutcome.Fail("agent_operation_unavailable");
                return new ReliableJoinOutcome(null, meeting, agent, resultConnection, initial, true,
                    resultConnection.State == AgentConnectionState.Activating, request.JoinMode == "takeover" ? "taken_over" : "created");
            }

            // Receipt lookup precedes transport occupancy; new operations never displace a different binding.
            if (connectionRegistry.ContainsTransport(session)) return ReliableJoinOutcome.Fail("connection_invalid");
            var existing = await participants.FindByNormalizedNameAsync(meeting.ID, name, token);
            if (existing is not null && existing.ParticipantType != ParticipantType.Agent) return ReliableJoinOutcome.Fail("agent_name_in_use");
            var takeover = request.JoinMode == AgentReliabilityProtocol.Takeover;
            if (takeover && existing is null) return ReliableJoinOutcome.Fail("agent_not_found");
            var existingCursor = existing is null ? null : await agentRepository.FindCursorAsync(meeting.ID, existing.ID, token);
            var previous = existingCursor?.CurrentConnectionId is Guid currentId
                ? await agentRepository.FindConnectionAsync(meeting.ID, currentId, token) : null;
            if (existing is not null && (existingCursor is null || previous is null)) return ReliableJoinOutcome.Fail("agent_operation_unavailable");
            if (existing is not null && !takeover)
            {
                var active = previous!.State == AgentConnectionState.Activating
                    || (previous.State == AgentConnectionState.Connected
                        && connectionRegistry.IsRegistered(meeting.ID, existing.ID, previous.ID, previous.McpTransportBinding)
                        && (existingCursor!.RequiredReliabilityMode == AgentReliabilityProtocol.Legacy || previous.LeaseExpiresAtUtc > UtcNow()));
                if (active) return ReliableJoinOutcome.Fail("agent_connection_already_active");
                if (previous.State == AgentConnectionState.Connected)
                    await DisconnectForRecoveryAsync(previous, AgentTaskFailureCode.ConnectionLeaseExpired, retire, token);
                return ReliableJoinOutcome.Fail("agent_takeover_required");
            }
            var now = UtcNow();
            var sequence = await messages.GetSequenceSummaryAsync(meeting.ID, token);
            var context = await contextBuilder.BuildInitialAsync(meeting.ID, sequence.HighWatermarkSequence, request.ContextCharacterLimit, token);
            var agentIdentity = existing ?? MeetingParticipant.CreateAgent(meeting.ID, displayName, now);
            var currentCursor = existingCursor ?? AgentContextCursor.Create(meeting.ID, agentIdentity.ID, sequence.HighWatermarkSequence, now);
            var next = AgentConnectionSession.CreateActivating(meeting.ID, agentIdentity.ID, request.ContextCharacterLimit,
                binding, currentCursor.NextConnectionGeneration(), AgentReliabilityProtocol.ExplicitAck,
                AgentRecoveryCredentials.HashKey(request.RecoveryKey!), now);
            if (previous is not null)
            {
                if (!await recoveryRepository.SupersedeAsync(Fence(previous), next.ID, now, token))
                    return ReliableJoinOutcome.Fail("agent_operation_conflict");
                retire.AddRange(connectionRegistry.ListRegistrations(meeting.ID).Where(item => item.ConnectionSessionId == previous.ID));
                await InvalidateUnfinishedTasksAsync(previous, AgentTaskFailureCode.ConnectionTakenOver, now, token);
            }
            currentCursor.AssignConnection(next, now);
            var initialDelivery = AgentInitialContextDelivery.Create(meeting.ID, agentIdentity.ID, next.ID, Window(context), now);
            if (existing is null) { unitOfWork.RegisterAdd(agentIdentity); unitOfWork.RegisterAdd(currentCursor); }
            else unitOfWork.RegisterEdit(currentCursor);
            unitOfWork.RegisterAdd(next);
            unitOfWork.RegisterAdd(initialDelivery);
            unitOfWork.RegisterAdd(AgentRecoveryOperation.ForJoin(meeting.ID, agentIdentity.ID, name, request.OperationId!.Value,
                fingerprint, next.ID, next.ConnectionGeneration, next.BindingVersion, binding, now));
            return new ReliableJoinOutcome(null, meeting, agentIdentity, next, initialDelivery, false, true, takeover ? "taken_over" : "created");
        }, cancellationToken);
        foreach (var previous in retire) connectionRegistry.Retire(previous);
        await PublishChangedActivitiesAsync(preflight.ID);
        if (outcome.Response is not null) return outcome.Response;
        var registration = Registration(outcome.Connection!);
        if (outcome.NeedsActivation)
        {
            RetireOlderRegistrations(registration);
            var activated = false;
            try
            {
                if (connectionRegistry.TryActivate(session, registration, null))
                    activated = await transactionRunner.RunAsync(registration.MeetingId, async token =>
                    {
                        if (!connectionRegistry.IsRegistered(registration.MeetingId, registration.ParticipantId, registration.ConnectionSessionId, binding)) return false;
                        if (!await recoveryRepository.ActivateAsync(ToFence(registration), UtcNow(), options.ConnectionLeaseSeconds, token)) return false;
                        var cursor = await agentRepository.FindCursorAsync(registration.MeetingId, registration.ParticipantId, token);
                        unitOfWork.RegisterEdit(cursor!);
                        return true;
                    }, cancellationToken);
                activated = activated && connectionRegistry.IsRegistered(registration.MeetingId, registration.ParticipantId, registration.ConnectionSessionId, binding);
            }
            finally
            {
                if (!activated)
                {
                    connectionRegistry.Retire(registration);
                    await ReconcileOwnedBindingAsync(registration, AgentTaskFailureCode.TransportClosed, CancellationToken.None);
                }
            }
            if (!activated) return ReliableFailure("agent_operation_unavailable");
        }
        var current = await agentRepository.FindConnectionAsync(registration.MeetingId, registration.ConnectionSessionId, cancellationToken);
        if (!await IsCurrentConnection(current, registration, cancellationToken)) return ReliableFailure("connection_superseded");
        var initialContext = await contextBuilder.RebuildFrozenAsync(registration.MeetingId, outcome.Initial!.GetWindow(), cancellationToken);
        var roster = await participants.ListAsync(registration.MeetingId, cancellationToken);
        var tasks = await agentRepository.ListTasksAsync(registration.MeetingId, registration.ParticipantId, cancellationToken);
        var head = tasks.FirstOrDefault(task => task.State is not (AgentReplyTaskState.Replied or AgentReplyTaskState.Cancelled));
        var nextAction = outcome.Initial.State == InitialContextState.Prepared ? AgentReliabilityProtocol.ConfirmTool
            : head is { State: AgentReplyTaskState.Processing } or { RetryExhausted: true } ? "get_agent_status" : AgentCollaborationContract.WaitToolName;
        var dto = new AgentJoinResultDto(new(outcome.Meeting!.ID, outcome.Meeting.Name, outcome.Meeting.Description),
            ToParticipantDto(outcome.Agent!), ReliableConnectionDto(current!, outcome.Initial), roster.Select(ToParticipantDto).ToArray(),
            AgentResidentInstruction.ReliableVersion, AgentResidentInstruction.BuildReliable(outcome.Meeting.Name, outcome.Agent!.DisplayName),
            initialContext, nextAction)
        {
            Kind = "joined", JoinDisposition = outcome.Disposition, Replayed = outcome.Replayed,
            InitialDeliveryId = outcome.Initial.ID, ContextPurpose = "initial", LeasePolicy = options.ToLeasePolicy(),
            Capabilities = AgentReliabilityProtocol.Capabilities
        };
        await PublishActivityAsync(registration.MeetingId, registration.ParticipantId, notifyPresence: true);
        var finalCursor = await agentRepository.FindCursorAsync(registration.MeetingId, registration.ParticipantId, cancellationToken);
        var finalError = ReliableConnectionError(current!, finalCursor, registration, UtcNow());
        if (finalError is not null) return finalError;
        return AgentOperationResult.Success(dto, "The Agent joined the meeting.");
    }

    private async Task DisconnectForRecoveryAsync(AgentConnectionSession connection, AgentTaskFailureCode reason,
        List<AgentConnectionRegistration> retire, CancellationToken cancellationToken)
    {
        var now = UtcNow();
        if (!await recoveryRepository.DisconnectAsync(Fence(connection), AgentTaskFailurePolicy.ToStoredCode(reason), now, cancellationToken)) return;
        await InvalidateUnfinishedTasksAsync(connection, reason, now, cancellationToken);
        retire.AddRange(connectionRegistry.ListRegistrations(connection.MeetingId).Where(item => item.ConnectionSessionId == connection.ID));
    }

    private async Task InvalidateUnfinishedTasksAsync(AgentConnectionSession connection, AgentTaskFailureCode reason,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var policy = new AgentTaskFailurePolicy(options.MaximumAutomaticRetries, options.RetryBackoffSeconds);
        foreach (var task in await agentRepository.ListTasksAsync(connection.MeetingId, connection.ParticipantId, cancellationToken))
        {
            var legacyFailed = connection.ReliabilityMode == AgentReliabilityProtocol.Legacy && task.State == AgentReplyTaskState.Failed;
            if (connection.ReliabilityMode == AgentReliabilityProtocol.Legacy && task.InitializeLegacyFailureDecision())
                unitOfWork.RegisterEdit(task);
            if (task.State is not (AgentReplyTaskState.Delivered or AgentReplyTaskState.Processing) && !legacyFailed) continue;
            if (task.CurrentAttemptId is Guid attemptId)
            {
                var attempt = await agentRepository.FindDeliveryAttemptAsync(connection.MeetingId, task.ID, attemptId, cancellationToken);
                if (attempt is not null && attempt.Invalidate(reason, now)) unitOfWork.RegisterEdit(attempt);
            }
            // Legacy failures predate explicit invalidation. Retire their old attempt so
            // an explicit retry can replace it, without rewriting the task's saved decision.
            if (!legacyFailed && task.FailReliable(reason, policy, now)) unitOfWork.RegisterEdit(task);
        }
    }

    private static AgentContextWindow Window(AgentContextDto context) => new(context.SnapshotSequence,
        context.CandidateFirstSequence, context.CandidateLastSequence, context.DeliveredFirstSequence, context.DeliveredLastSequence,
        context.DeclaredCharacterLimit, context.DeliveredCharacterCount, context.Truncated, context.OverLimit,
        context.TruncatedMessageCount, context.ContextFormatVersion);
    private void RetireOlderRegistrations(AgentConnectionRegistration current)
    {
        // The durable ownership transaction has committed and the participant lock is
        // still held. A previous request may have stopped before retiring its bindings.
        // Only retire versions strictly older than this committed current owner.
        foreach (var previous in connectionRegistry.ListRegistrations(current.MeetingId))
            if (previous.ParticipantId == current.ParticipantId
                && (previous.ConnectionGeneration < current.ConnectionGeneration
                    || previous.ConnectionSessionId == current.ConnectionSessionId
                        && previous.ConnectionGeneration == current.ConnectionGeneration
                        && previous.BindingVersion < current.BindingVersion))
                connectionRegistry.Retire(previous);
    }

    private static AgentConnectionFence Fence(AgentConnectionSession connection) => new(connection.MeetingId, connection.ParticipantId,
        connection.ID, connection.ConnectionGeneration, connection.BindingVersion, connection.McpTransportBinding);
    private static AgentConnectionRegistration Registration(AgentConnectionSession connection) => new(connection.MeetingId,
        connection.ParticipantId, connection.ID, connection.McpTransportBinding)
    { ConnectionGeneration = connection.ConnectionGeneration, BindingVersion = connection.BindingVersion,
        ReliabilityMode = connection.ReliabilityMode };
    private static AgentConnectionDto ReliableConnectionDto(AgentConnectionSession connection, AgentInitialContextDelivery initial) => new(
        connection.ID, ToWire(connection.State), connection.DeclaredContextCharacterLimit, ToWire(initial.State))
    {
        ReliabilityMode = connection.ReliabilityMode, ConnectionGeneration = connection.ConnectionGeneration,
        BindingVersion = connection.BindingVersion, LeaseExpiresAt = connection.LeaseExpiresAtUtc, DisconnectReason = connection.DisconnectReason
    };
    private static AgentOperationResult ReliableFailure(string code, string nextAction = "stop") =>
        AgentOperationResult.Failure(code, "The Agent operation could not be completed.", new(nextAction));
    private sealed record ReliableJoinOutcome(AgentOperationResult? Response, Meeting? Meeting, MeetingParticipant? Agent,
        AgentConnectionSession? Connection, AgentInitialContextDelivery? Initial, bool Replayed, bool NeedsActivation, string? Disposition)
    {
        public static ReliableJoinOutcome Fail(string code) => new(ReliableFailure(code), null, null, null, null, false, false, null);
        public static ReliableJoinOutcome Recovery(Guid id) => new(AgentOperationResult.Success(new AgentJoinRecoveryRequiredDto(id, true),
            "The committed Agent connection requires recovery."), null, null, null, null, true, false, null);
    }
}
