using System.Diagnostics;
using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Domain.Exceptions;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Domain.UnitOfWork;
using TransportAuthorKind = RoundTableMeeting.Abstraction.Contracts.RealtimeChat.MessageAuthorKind;

namespace RoundTableMeeting.Application.AgentCollaboration;

public sealed class AgentCollaborationService(
    AgentCollaborationOptions options,
    IMeetingRepository meetings,
    IMeetingParticipantRepository participants,
    IMessageRepository messages,
    IAgentCollaborationRepository agentRepository,
    IUnitOfWork unitOfWork,
    IMeetingTransactionRunner transactionRunner,
    IMeetingRealtimeGate realtimeGate,
    IAgentJoinCoordinator joinCoordinator,
    IAgentJoinRateLimiter joinRateLimiter,
    IAgentConnectionRegistry connectionRegistry,
    IAgentConnectionCompensator connectionCompensator,
    AgentContextBuilder contextBuilder,
    IResolvedMessageAppendCore messageAppendCore,
    IAgentTaskSignalRegistry taskSignals,
    IAgentActivityService activityService,
    IAgentActivityNotifier activityNotifier,
    TimeProvider timeProvider) : IAgentCollaborationService
{
    public async Task<AgentOperationResult> JoinAsync(
        AgentJoinRequest request,
        McpTransportSessionKey? transportSession,
        CancellationToken cancellationToken = default)
    {
        if (transportSession is not { IsEmpty: false } session)
            return Failure("connection_invalid", "The MCP protocol session is unavailable.");
        if (connectionRegistry.ContainsTransport(session))
            return Failure("connection_invalid", "The MCP protocol session is already bound.");
        if (!joinRateLimiter.TryAcquire(
                request.SourceAddress,
                request.MeetingCode,
                request.DisplayName,
                session,
                out _))
            return Failure("agent_operation_unavailable", "The Agent join is temporarily unavailable.");
        if (!TryParseMeetingCode(request.MeetingCode, out var meetingCode))
            return Failure("meeting_not_found", "The meeting is unavailable.");
        if (string.IsNullOrWhiteSpace(request.DisplayName))
            return Failure("agent_name_required", "The Agent display name is required.");

        string displayName;
        try
        {
            displayName = MeetingParticipant.NormalizeDisplayName(request.DisplayName);
        }
        catch (ArgumentException)
        {
            return Failure("agent_name_invalid", "The Agent display name is invalid.");
        }

        if (!IsContextLimitValid(request.ContextCharacterLimit))
            return Failure("context_limit_invalid", "The context character limit is invalid.");

        var preflightMeeting = await meetings.FindActiveByCodeAsync(meetingCode, cancellationToken);
        if (preflightMeeting is null)
            return Failure("meeting_not_found", "The meeting is unavailable.");

        using var sharedLease = await realtimeGate.AcquireSharedAsync(preflightMeeting.ID, cancellationToken);
        if (sharedLease is null)
            return Failure("meeting_not_found", "The meeting is unavailable.");

        var normalizedDisplayName = displayName.ToUpperInvariant();
        await using var joinLease = await joinCoordinator.AcquireAsync(
            preflightMeeting.ID, normalizedDisplayName, cancellationToken);
        var transportBinding = session.ToStorageBinding();
        JoinTransactionResult outcome;
        try
        {
            outcome = await transactionRunner.RunAsync(async token =>
            {
                var meeting = await meetings.FindActiveByCodeAsync(meetingCode, token);
                if (meeting is null || meeting.ID != preflightMeeting.ID)
                    return JoinTransactionResult.Failure("meeting_not_found");

                var existing = await participants.FindByNormalizedNameAsync(
                    meeting.ID, normalizedDisplayName, token);
                if (existing is not null)
                {
                    if (existing.ParticipantType != ParticipantType.Agent)
                        return JoinTransactionResult.Failure("agent_name_in_use");

                    var activeConnection = await agentRepository.FindConnectedConnectionAsync(
                        meeting.ID, existing.ID, token);
                    if (activeConnection is not null
                        && connectionRegistry.IsRegistered(
                            meeting.ID, existing.ID, activeConnection.ID, activeConnection.McpTransportBinding))
                        return JoinTransactionResult.Failure("agent_connection_already_active");

                    if (activeConnection is not null)
                    {
                        await agentRepository.DisconnectConnectionAsync(
                            meeting.ID,
                            existing.ID,
                            activeConnection.ID,
                            activeConnection.McpTransportBinding,
                            UtcNow(),
                            token);
                    }
                    return JoinTransactionResult.Failure("agent_recovery_not_supported");
                }

                var now = UtcNow();
                var sequence = await messages.GetSequenceSummaryAsync(meeting.ID, token);
                var agent = MeetingParticipant.CreateAgent(meeting.ID, displayName, now);
                var connection = AgentConnectionSession.Create(
                    meeting.ID,
                    agent.ID,
                    request.ContextCharacterLimit,
                    sequence.HighWatermarkSequence,
                    transportBinding,
                    now);
                var cursor = AgentContextCursor.Create(
                    meeting.ID, agent.ID, sequence.HighWatermarkSequence, now);
                var initialContext = await contextBuilder.BuildInitialAsync(
                    meeting.ID, sequence.HighWatermarkSequence, request.ContextCharacterLimit, token);
                var currentParticipants = await participants.ListAsync(meeting.ID, token);

                unitOfWork.RegisterAdd(agent);
                unitOfWork.RegisterAdd(connection);
                unitOfWork.RegisterAdd(cursor);

                return JoinTransactionResult.Success(
                    meeting, agent, connection, currentParticipants, initialContext);
            }, cancellationToken);
        }
        catch (MeetingJoinConflictException)
        {
            return await ResolveJoinConflictAsync(
                preflightMeeting.ID, normalizedDisplayName, cancellationToken);
        }
        catch (AgentCollaborationConflictException)
        {
            return Failure("agent_operation_conflict", "The Agent join conflicted with another operation.");
        }

        if (!outcome.Succeeded) return MapTransactionFailure(outcome.ErrorCode!);

        var registration = new AgentConnectionRegistration(
            outcome.Meeting!.ID,
            outcome.Agent!.ID,
            outcome.Connection!.ID,
            transportBinding);
        if (!connectionRegistry.TryRegister(session, registration))
        {
            connectionRegistry.Unregister(session, registration.ConnectionSessionId);
            await connectionCompensator.DisconnectAsync(registration, UtcNow(), CancellationToken.None);
            return Failure("agent_operation_unavailable", "The Agent connection could not be registered.");
        }
        await PublishActivityAsync(registration.MeetingId, registration.ParticipantId, notifyPresence: true);

        return AgentOperationResult.Success(BuildJoinResult(outcome), "The Agent joined the meeting.");
    }

    public async Task<AgentOperationResult> WaitAsync(
        AgentWaitRequest request,
        McpTransportSessionKey? transportSession,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveRegistration(request.ConnectionSessionId, transportSession, out var registration))
            return Failure("connection_invalid", "The connection session is invalid.");

        var confirmationError = await ConfirmInitialContextAsync(registration, cancellationToken);
        if (confirmationError is not null)
            return Failure(confirmationError, confirmationError == "meeting_not_found"
                ? "The meeting is unavailable." : "The connection session is invalid.");

        var stopwatch = Stopwatch.StartNew();
        var timeout = TimeSpan.FromSeconds(options.WaitTimeoutSeconds);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Observe before probing the database: deletion/task creation between
            // the probe and the wait must not lose its wakeup.
            using var observationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var observation = taskSignals.WaitAsync(registration.MeetingId, registration.ParticipantId,
                timeout - stopwatch.Elapsed, observationCancellation.Token);
            try
            {
                WaitProbe probe;
                try
                {
                    probe = await PrepareDeliveryAsync(registration, transportSession!.Value, cancellationToken);
                }
                catch (AgentCollaborationConflictException)
                {
                    return Failure("agent_operation_conflict", "The Agent wait conflicted with another operation.");
                }
                catch (ArgumentException)
                {
                    return Failure("agent_operation_unavailable", "The Agent task could not be prepared.");
                }

                if (!probe.Succeeded)
                {
                    await PublishActivityAsync(registration.MeetingId, registration.ParticipantId);
                    return Failure(probe.ErrorCode!, probe.ErrorCode == "meeting_not_found"
                        ? "The meeting is unavailable."
                        : "The Agent task is not available.");
                }

                if (probe.HasTask)
                {
                    var result = new AgentMentionResultDto(
                        "mention", probe.TaskId, "processing", probe.TriggerMessageId,
                        probe.TriggerSequence, probe.TriggerMentionId, probe.SourceParticipantId,
                        probe.DeliveryAttemptId, probe.ReplyIdempotencyKey, probe.Context!,
                        AgentCollaborationContract.ReplyToolName);
                    bool finalized;
                    try
                    {
                        finalized = await FinalizeDeliveryAsync(probe, registration, transportSession.Value, cancellationToken);
                    }
                    catch (AgentCollaborationConflictException)
                    {
                        return Failure("agent_operation_conflict", "The Agent wait conflicted with another operation.");
                    }

                    if (!finalized)
                        return Failure("agent_operation_unavailable", "The Agent task could not be delivered.");

                    await PublishActivityAsync(registration.MeetingId, registration.ParticipantId);

                    return AgentOperationResult.Success(
                        result,
                        "An Agent mention is ready for processing.");
                }

                var remaining = timeout - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    return AgentOperationResult.Success(
                        new AgentWaitIdleDto("idle", AgentCollaborationContract.WaitToolName),
                        "No Agent mention arrived before the wait timeout.");
                }

                await observation;
                // Always probe again after timeout, wakeup or deletion before returning idle.
            }
            finally
            {
                await observationCancellation.CancelAsync();
                try { await observation; }
                catch (OperationCanceledException) when (observationCancellation.IsCancellationRequested) { }
            }
        }
    }

    public async Task<AgentOperationResult> ReplyAsync(
        AgentReplyRequest request,
        McpTransportSessionKey? transportSession,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveRegistration(request.ConnectionSessionId, transportSession, out var registration))
            return Failure("connection_invalid", "The connection session is invalid.");
        if (request.TaskId is not Guid taskId || taskId == Guid.Empty)
            return Failure("task_not_available", "The Agent task is not available.");
        if (request.DeliveryAttemptId is not Guid deliveryAttemptId || deliveryAttemptId == Guid.Empty)
            return Failure("delivery_attempt_invalid", "The delivery attempt is invalid.");
        if (request.ReplyIdempotencyKey is not Guid replyIdempotencyKey || replyIdempotencyKey == Guid.Empty)
            return Failure("reply_idempotency_mismatch", "The reply idempotency key does not match the task.");

        string content;
        try
        {
            content = MeetingMessage.NormalizeContent(request.Content);
            MeetingMessage.ValidateContentLength(content, options.ReplyCharacterLimit);
        }
        catch (ArgumentException)
        {
            return Failure("message_validation_failed", "The Agent reply is invalid.");
        }

        using var sharedLease = await realtimeGate.AcquireSharedAsync(registration.MeetingId, cancellationToken);
        if (sharedLease is null)
            return Failure("meeting_not_found", "The meeting is unavailable.");

        ReplyTransactionResult outcome;
        try
        {
            outcome = await transactionRunner.RunAsync(async token =>
            {
                if (await meetings.FindActiveByIdAsync(registration.MeetingId, token) is null)
                    return ReplyTransactionResult.Failure("meeting_not_found");

                var connection = await agentRepository.FindConnectionAsync(
                    registration.MeetingId,
                    registration.ConnectionSessionId,
                    token);
                if (!IsCurrentConnection(connection, registration))
                    return ReplyTransactionResult.Failure("connection_invalid");

                var task = await agentRepository.FindTaskForAgentAsync(
                    registration.MeetingId,
                    registration.ParticipantId,
                    taskId,
                    token);
                if (task is null)
                    return ReplyTransactionResult.Failure("task_not_available");
                if (task.ReplyIdempotencyKey != replyIdempotencyKey)
                    return ReplyTransactionResult.Failure("reply_idempotency_mismatch");

                if (task.State is not AgentReplyTaskState.Processing and not AgentReplyTaskState.Replied)
                    return ReplyTransactionResult.Failure("task_not_available");
                var attempt = await agentRepository.FindDeliveryAttemptAsync(
                    registration.MeetingId, task.ID, deliveryAttemptId, token);
                if (attempt is null || attempt.ConnectionSessionId != registration.ConnectionSessionId
                    || attempt.State != AgentTaskDeliveryAttemptState.Delivered)
                    return ReplyTransactionResult.Failure("delivery_attempt_invalid");

                if (task.State == AgentReplyTaskState.Replied)
                {
                    if (task.ReplyMessageId is not Guid existingMessageId || existingMessageId == Guid.Empty)
                        return ReplyTransactionResult.Failure("reply_idempotency_mismatch");
                    var existingMessage = await messages.FindByIdAsync(
                        registration.MeetingId,
                        existingMessageId,
                        token);
                    return existingMessage is not null
                        && existingMessage.AuthorKind == RoundTableMeeting.Domain.Enums.MessageAuthorKind.Agent
                        && existingMessage.AuthorParticipantId == registration.ParticipantId
                        && existingMessage.ClientSubmissionId == task.ReplyIdempotencyKey
                        && string.Equals(existingMessage.Content, content, StringComparison.Ordinal)
                        ? ReplyTransactionResult.Replay(ToAppendResult(existingMessage))
                        : ReplyTransactionResult.Failure("reply_idempotency_mismatch");
                }

                var agent = (await participants.ListAsync(registration.MeetingId, token))
                    .SingleOrDefault(item => item.ID == registration.ParticipantId && item.ParticipantType == ParticipantType.Agent);
                if (agent is null)
                    return ReplyTransactionResult.Failure("connection_invalid");

                var message = await messageAppendCore.AppendAgentAsync(
                    registration.MeetingId,
                    agent.ID,
                    agent.DisplayName,
                    content,
                    task.ReplyIdempotencyKey,
                    token);
                task.MarkReplied(message.MessageId, UtcNow());
                unitOfWork.RegisterEdit(task);
                return ReplyTransactionResult.Created(message);
            }, cancellationToken);
        }
        catch (AgentCollaborationConflictException)
        {
            return Failure("agent_operation_conflict", "The Agent reply conflicted with another operation.");
        }
        catch (ArgumentException)
        {
            return Failure("message_validation_failed", "The Agent reply is invalid.");
        }

        if (!outcome.Succeeded)
            return outcome.ErrorCode switch
            {
                "meeting_not_found" => Failure("meeting_not_found", "The meeting is unavailable."),
                "connection_invalid" => Failure("connection_invalid", "The connection session is invalid."),
                "delivery_attempt_invalid" => Failure("delivery_attempt_invalid", "The delivery attempt is invalid."),
                "reply_idempotency_mismatch" => Failure("reply_idempotency_mismatch", "The reply idempotency key or content does not match the task."),
                _ => Failure("task_not_available", "The Agent task is not available.")
            };

        var messageDto = await ToMessageDtoAsync(outcome.Message!, cancellationToken);
        taskSignals.Signal(registration.MeetingId, registration.ParticipantId);
        await PublishActivityAsync(registration.MeetingId, registration.ParticipantId);
        return AgentOperationResult.Success(
            new AgentReplyResultDto(messageDto, outcome.IsReplay, AgentCollaborationContract.WaitToolName),
            outcome.IsReplay ? "The Agent reply was already accepted." : "The Agent reply was accepted.");
    }

    public async Task<AgentOperationResult> GetStatusAsync(
        AgentStatusRequest request,
        McpTransportSessionKey? transportSession,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveRegistration(request.ConnectionSessionId, transportSession, out var registration))
            return Failure("connection_invalid", "The connection session is invalid.");

        using var sharedLease = await realtimeGate.AcquireSharedAsync(registration.MeetingId, cancellationToken);
        if (sharedLease is null)
            return Failure("meeting_not_found", "The meeting is unavailable.");
        if (await meetings.FindActiveByIdAsync(registration.MeetingId, cancellationToken) is null)
            return Failure("meeting_not_found", "The meeting is unavailable.");

        var connection = await agentRepository.FindConnectionAsync(
            registration.MeetingId,
            registration.ConnectionSessionId,
            cancellationToken);
        if (connection is null || !IsCurrentConnection(connection, registration))
            return Failure("connection_invalid", "The connection session is invalid.");

        var cursor = await agentRepository.FindCursorAsync(
            registration.MeetingId,
            registration.ParticipantId,
            cancellationToken);
        if (cursor is null)
            return Failure("connection_invalid", "The connection session is invalid.");

        var tasks = await agentRepository.ListTasksAsync(
            registration.MeetingId,
            registration.ParticipantId,
            cancellationToken);
        var counts = Enum.GetValues<AgentReplyTaskState>()
            .ToDictionary(state => state.ToString().ToLowerInvariant(), state => tasks.Count(task => task.State == state), StringComparer.Ordinal);
        var head = tasks.FirstOrDefault(task => task.State is not AgentReplyTaskState.Replied and not AgentReplyTaskState.Cancelled);
        var data = new AgentStatusResultDto(
            registration.MeetingId,
            registration.ParticipantId,
            new(connection.ID, ToWire(connection.State), connection.DeclaredContextCharacterLimit, ToWire(connection.InitialContextState)),
            counts,
            head is null ? null : new(head.ID, head.TriggerSequence, ToWire(head.State),
                head.State == AgentReplyTaskState.Delivered ? "delivery_not_finalized" : head.FailureCode),
            cursor.DeliveredThroughSequence,
            cursor.AllocatedThroughSequence);
        return AgentOperationResult.Success(data, "The Agent status was read.");
    }

    private async Task<string?> ConfirmInitialContextAsync(
        AgentConnectionRegistration registration,
        CancellationToken cancellationToken)
    {
        using var lease = await realtimeGate.AcquireSharedAsync(registration.MeetingId, cancellationToken);
        if (lease is null) return "meeting_not_found";
        return await transactionRunner.RunAsync<string?>(async token =>
        {
            if (await meetings.FindActiveByIdAsync(registration.MeetingId, token) is null)
                return "meeting_not_found";
            var connection = await agentRepository.FindConnectionAsync(registration.MeetingId, registration.ConnectionSessionId, token);
            if (!IsCurrentConnection(connection, registration)) return "connection_invalid";
            var cursor = await agentRepository.FindCursorAsync(registration.MeetingId, registration.ParticipantId, token);
            if (cursor is null) return "connection_invalid";
            if (connection!.InitialContextState == InitialContextState.Prepared)
            {
                var now = UtcNow();
                connection.ConfirmInitialContext(now);
                unitOfWork.RegisterEdit(connection);
                if (cursor.AdvanceDeliveredThrough(connection.InitialSnapshotSequence, now))
                    unitOfWork.RegisterEdit(cursor);
            }
            return null;
        }, cancellationToken);
    }

    private async Task<WaitProbe> PrepareDeliveryAsync(
        AgentConnectionRegistration registration,
        McpTransportSessionKey transportSession,
        CancellationToken cancellationToken)
    {
        using var sharedLease = await realtimeGate.AcquireSharedAsync(registration.MeetingId, cancellationToken);
        if (sharedLease is null)
            return WaitProbe.Failure("meeting_not_found");

        return await transactionRunner.RunAsync(async token =>
        {
            if (await meetings.FindActiveByIdAsync(registration.MeetingId, token) is null)
                return WaitProbe.Failure("meeting_not_found");

            var connection = await agentRepository.FindConnectionAsync(
                registration.MeetingId,
                registration.ConnectionSessionId,
                token);
            if (connection is null || !IsCurrentConnection(connection, registration))
                return WaitProbe.Failure("connection_invalid");

            var cursor = await agentRepository.FindCursorAsync(
                registration.MeetingId,
                registration.ParticipantId,
                token);
            if (cursor is null)
                return WaitProbe.Failure("connection_invalid");

            var now = UtcNow();
            var tasks = await agentRepository.ListTasksAsync(
                registration.MeetingId,
                registration.ParticipantId,
                token);
            var head = tasks.FirstOrDefault(task => task.State is not AgentReplyTaskState.Replied and not AgentReplyTaskState.Cancelled);
            if (head is null)
                return WaitProbe.Empty();
            if (head.State != AgentReplyTaskState.Queued)
                return WaitProbe.Failure("task_not_available");

            AgentContextDto context;
            try
            {
                context = await contextBuilder.BuildTaskAsync(registration.MeetingId,
                    head.CandidateFromSequenceExclusive, head.CandidateToSequenceInclusive,
                    connection.DeclaredContextCharacterLimit, token);
            }
            catch (ArgumentException)
            {
                head.MarkFailed("context_invalid", now);
                unitOfWork.RegisterEdit(head);
                return WaitProbe.Failure("task_not_available");
            }
            if (context.CandidateFirstSequence is not long candidateFirst
                || context.CandidateLastSequence is not long candidateLast
                || context.DeliveredFirstSequence is not long deliveredFirst
                || context.DeliveredLastSequence is not long deliveredLast)
                return WaitProbe.Failure("agent_operation_unavailable");

            var attempt = AgentTaskDeliveryAttempt.Create(
                head.ID,
                connection.ID,
                registration.MeetingId,
                candidateFirst,
                candidateLast,
                connection.DeclaredContextCharacterLimit,
                context.DeliveredCharacterCount,
                context.Truncated,
                context.OverLimit,
                context.TruncatedMessageCount,
                now);
            head.MarkDelivered(now);
            unitOfWork.RegisterEdit(head);
            unitOfWork.RegisterAdd(attempt);
            return WaitProbe.Task(
                head,
                attempt,
                context,
                deliveredFirst,
                deliveredLast);
        }, cancellationToken);
    }

    private async Task<bool> FinalizeDeliveryAsync(
        WaitProbe probe,
        AgentConnectionRegistration registration,
        McpTransportSessionKey transportSession,
        CancellationToken cancellationToken)
    {
        using var sharedLease = await realtimeGate.AcquireSharedAsync(registration.MeetingId, cancellationToken);
        if (sharedLease is null)
            return false;

        return await transactionRunner.RunAsync(async token =>
        {
            if (await meetings.FindActiveByIdAsync(registration.MeetingId, token) is null)
                return false;
            var connection = await agentRepository.FindConnectionAsync(
                registration.MeetingId,
                registration.ConnectionSessionId,
                token);
            if (!IsCurrentConnection(connection, registration))
                return false;
            var task = await agentRepository.FindTaskForAgentAsync(
                registration.MeetingId,
                registration.ParticipantId,
                probe.TaskId,
                token);
            var attempt = await agentRepository.FindDeliveryAttemptAsync(
                registration.MeetingId,
                probe.TaskId,
                probe.DeliveryAttemptId,
                token);
            var cursor = await agentRepository.FindCursorAsync(
                registration.MeetingId,
                registration.ParticipantId,
                token);
            if (task is null
                || attempt is null
                || cursor is null
                || task.State != AgentReplyTaskState.Delivered
                || attempt.State != AgentTaskDeliveryAttemptState.Prepared)
                return false;

            var now = UtcNow();
            task.MarkProcessing(now);
            attempt.MarkDelivered(probe.DeliveredFirstSequence, probe.DeliveredLastSequence, now);
            cursor.AdvanceDeliveredThrough(probe.DeliveredLastSequence, now);
            unitOfWork.RegisterEdit(task);
            unitOfWork.RegisterEdit(attempt);
            unitOfWork.RegisterEdit(cursor);
            return true;
        }, cancellationToken);
    }

    private bool TryResolveRegistration(
        Guid? connectionSessionId,
        McpTransportSessionKey? transportSession,
        out AgentConnectionRegistration registration)
    {
        registration = default!;
        return transportSession is { IsEmpty: false } session
            && connectionSessionId is Guid requestedId
            && requestedId != Guid.Empty
            && connectionRegistry.TryGetRegistration(session, out registration)
            && registration.ConnectionSessionId == requestedId;
    }

    private static bool IsCurrentConnection(
        AgentConnectionSession? connection,
        AgentConnectionRegistration registration)
        => connection is not null
            && connection.State == AgentConnectionState.Connected
            && connection.MeetingId == registration.MeetingId
            && connection.ParticipantId == registration.ParticipantId
            && connection.ID == registration.ConnectionSessionId
            && string.Equals(connection.McpTransportBinding, registration.TransportBinding, StringComparison.Ordinal);

    private static string ToWire(AgentConnectionState state) => state.ToString().ToLowerInvariant();

    private static string ToWire(InitialContextState state) => state.ToString().ToLowerInvariant();

    private static string ToWire(AgentReplyTaskState state) => state.ToString().ToLowerInvariant();

    private async Task<MessageDto> ToMessageDtoAsync(
        ResolvedMessageAppendResult message,
        CancellationToken cancellationToken)
    {
        var mentions = await messages.ListMentionsAsync(message.MeetingId, message.MessageId, cancellationToken);
        return new MessageDto(
            message.MessageId,
            message.MeetingId,
            message.Sequence,
            new MessageAuthorDto(message.ParticipantId, TransportAuthorKind.Agent, message.AuthorDisplayName),
            message.Content,
            mentions.Select(item => new MessageMentionDto(item.TargetParticipantId, item.TargetDisplayName, item.StartOffset, item.Length)).ToArray(),
            message.CreatedAtUtc);
    }

    private static ResolvedMessageAppendResult ToAppendResult(MeetingMessage message)
        => new(
            message.ID,
            message.MeetingId,
            message.Sequence,
            message.AuthorParticipantId!.Value,
            message.AuthorDisplayName,
            message.Content,
            message.CreatedAtUtc);

    private async Task<AgentOperationResult> ResolveJoinConflictAsync(
        Guid meetingId,
        string normalizedDisplayName,
        CancellationToken cancellationToken)
    {
        var participant = await participants.FindByNormalizedNameAsync(
            meetingId, normalizedDisplayName, cancellationToken);
        if (participant is null)
            return Failure("agent_operation_conflict", "The Agent join conflicted with another operation.");
        if (participant.ParticipantType != ParticipantType.Agent)
            return Failure("agent_name_in_use", "The display name is already in use.");
        var connection = await agentRepository.FindConnectedConnectionAsync(
            meetingId, participant.ID, cancellationToken);
        return connection is not null
            && connectionRegistry.IsRegistered(meetingId, participant.ID, connection.ID, connection.McpTransportBinding)
            ? Failure("agent_connection_already_active", "The Agent already has an active connection.")
            : Failure("agent_recovery_not_supported", "Agent recovery is not supported in this version.");
    }

    private static AgentJoinResultDto BuildJoinResult(JoinTransactionResult outcome)
    {
        var meeting = outcome.Meeting!;
        var agent = outcome.Agent!;
        var connection = outcome.Connection!;
        var participantDtos = outcome.Participants!
            .Append(agent)
            .OrderBy(participant => participant.JoinedAtUtc)
            .ThenBy(participant => participant.ID)
            .Select(ToParticipantDto)
            .ToArray();
        return new(
            new(meeting.ID, meeting.Name, meeting.Description),
            ToParticipantDto(agent),
            new(connection.ID, "connected", connection.DeclaredContextCharacterLimit, "prepared"),
            participantDtos,
            AgentResidentInstruction.Version,
            AgentResidentInstruction.Build(meeting.Name, agent.DisplayName),
            outcome.InitialContext!,
            AgentCollaborationContract.WaitToolName);
    }

    private static AgentParticipantDto ToParticipantDto(MeetingParticipant participant)
        => new(participant.ID, participant.DisplayName, participant.ParticipantType == ParticipantType.Agent ? "agent" : "human");

    private AgentOperationResult MapTransactionFailure(string code)
        => code switch
        {
            "agent_name_in_use" => Failure(code, "The display name is already in use."),
            "agent_connection_already_active" => Failure(code, "The Agent already has an active connection."),
            "agent_recovery_not_supported" => Failure(code, "Agent recovery is not supported in this version."),
            _ => Failure("meeting_not_found", "The meeting is unavailable.")
        };

    private bool IsContextLimitValid(int value)
        => value >= options.MinimumContextCharacterLimit && value <= options.MaximumContextCharacterLimit;

    private static bool TryParseMeetingCode(string? value, out Guid meetingCode)
    {
        meetingCode = Guid.Empty;
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= 64
            && Guid.TryParseExact(value.Trim(), "D", out meetingCode);
    }

    private DateTimeOffset UtcNow() => timeProvider.GetUtcNow();

    private async Task PublishActivityAsync(Guid meetingId, Guid participantId, bool notifyPresence = false)
    {
        try
        {
            var activity = await activityService.FindAsync(meetingId, participantId, CancellationToken.None);
            if (activity is not null) await activityNotifier.NotifyAsync(activity, notifyPresence, CancellationToken.None);
        }
        catch
        {
            // The committed Agent operation remains authoritative; subscription snapshots recover activity later.
        }
    }

    private static AgentOperationResult Failure(string code, string message)
        => AgentOperationResult.Failure(code, message);

    private sealed record JoinTransactionResult(
        bool Succeeded,
        string? ErrorCode,
        Meeting? Meeting,
        MeetingParticipant? Agent,
        AgentConnectionSession? Connection,
        IReadOnlyList<MeetingParticipant>? Participants,
        AgentContextDto? InitialContext)
    {
        public static JoinTransactionResult Failure(string code)
            => new(false, code, null, null, null, null, null);

        public static JoinTransactionResult Success(
            Meeting meeting,
            MeetingParticipant agent,
            AgentConnectionSession connection,
            IReadOnlyList<MeetingParticipant> participants,
            AgentContextDto initialContext)
            => new(true, null, meeting, agent, connection, participants, initialContext);
    }

    private sealed record WaitProbe(
        bool Succeeded,
        string? ErrorCode,
        bool HasTask,
        Guid TaskId,
        Guid DeliveryAttemptId,
        Guid ReplyIdempotencyKey,
        Guid TriggerMessageId,
        long TriggerSequence,
        Guid TriggerMentionId,
        Guid? SourceParticipantId,
        AgentContextDto? Context,
        long DeliveredFirstSequence,
        long DeliveredLastSequence)
    {
        public static WaitProbe Empty() => new(true, null, false, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, 0, Guid.Empty, null, null, 0, 0);

        public static WaitProbe Failure(string code) => new(false, code, false, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, 0, Guid.Empty, null, null, 0, 0);

        public static WaitProbe Task(
            AgentReplyTask task,
            AgentTaskDeliveryAttempt attempt,
            AgentContextDto context,
            long deliveredFirstSequence,
            long deliveredLastSequence)
            => new(true, null, true, task.ID, attempt.ID, task.ReplyIdempotencyKey, task.TriggerMessageId, task.TriggerSequence, task.TriggerMentionId, task.SourceParticipantId, context, deliveredFirstSequence, deliveredLastSequence);
    }

    private sealed record ReplyTransactionResult(
        bool Succeeded,
        string? ErrorCode,
        ResolvedMessageAppendResult? Message,
        bool IsReplay)
    {
        public static ReplyTransactionResult Failure(string code) => new(false, code, null, false);

        public static ReplyTransactionResult Created(ResolvedMessageAppendResult message) => new(true, null, message, false);

        public static ReplyTransactionResult Replay(ResolvedMessageAppendResult message) => new(true, null, message, true);
    }
}
