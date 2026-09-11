using System.Diagnostics;
using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.AgentCollaboration;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.Application.AgentCollaboration;

public sealed partial class AgentCollaborationService
{
    public async Task<AgentOperationResult> WaitReliableAsync(AgentWaitRequest request, McpTransportSessionKey? transportSession,
        CancellationToken cancellationToken = default)
    {
        var seconds = request.WaitTimeoutSeconds ?? options.ReliableWaitTimeoutSeconds;
        if (seconds < 1 || seconds > options.WaitTimeoutSeconds) return ReliableFailure("agent_request_invalid");
        if (!TryResolveRegistration(request.ConnectionSessionId, transportSession, out var registration)) return ReliableFailure("connection_invalid");
        var bindingCancellation = connectionRegistry.GetBindingCancellationToken(transportSession!.Value, registration);
        var stopwatch = Stopwatch.StartNew();
        var timeout = TimeSpan.FromSeconds(seconds);
        var renewed = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var observationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, bindingCancellation);
            var observation = taskSignals.WaitAsync(registration.MeetingId, registration.ParticipantId,
                timeout - stopwatch.Elapsed, observationCancellation.Token);
            try
            {
                var result = await RunReliableAsync(request.ConnectionSessionId, transportSession, async (connection, cursor, token) =>
                {
                    var initial = await agentRepository.FindInitialContextAsync(connection.MeetingId, connection.ParticipantId, connection.ID, token);
                    if (initial?.State != InitialContextState.Confirmed) return ReliableFailure("initial_context_unconfirmed", AgentReliabilityProtocol.ConfirmTool);
                    var head = (await agentRepository.ListTasksAsync(connection.MeetingId, connection.ParticipantId, token))
                        .FirstOrDefault(task => task.State is not (AgentReplyTaskState.Replied or AgentReplyTaskState.Cancelled));
                    var now = UtcNow();
                    if (head is null)
                    {
                        if (!renewed) { ExtendLease(connection, now, seconds); renewed = true; }
                        return AgentOperationResult.Success(new AgentWaitIdleDto("idle", AgentCollaborationContract.WaitToolName), "No Agent task is ready.");
                    }
                    var attempt = head.CurrentAttemptId is Guid attemptId
                        ? await agentRepository.FindDeliveryAttemptAsync(connection.MeetingId, head.ID, attemptId, token) : null;
                    if (head.State == AgentReplyTaskState.Processing) return await TaskRejectionAsync("task_in_progress", head, token);
                    if (head.State == AgentReplyTaskState.Delivered)
                    {
                        if (attempt is null || !IsCurrentAttempt(connection, head, attempt)) return ReliableFailure("delivery_attempt_stale", "get_agent_status");
                        if (attempt.ConfirmationDeadlineUtc <= now)
                            FailCurrentAttempt(head, attempt, cursor, AgentTaskFailureCode.ConfirmationTimeout, now);
                        else
                        {
                            AgentContextDto context;
                            try { context = await contextBuilder.RebuildFrozenAsync(connection.MeetingId, AttemptWindow(attempt), token); }
                            catch (ArgumentException)
                            {
                                FailCurrentAttempt(head, attempt, cursor, AgentTaskFailureCode.ContextUnavailable, now);
                                return await TaskRejectionAsync("retry_exhausted", head, token);
                            }
                            if (!renewed) { ExtendLease(connection, now, seconds); renewed = true; }
                            return Mention(head, attempt, context, true, "response_replay");
                        }
                    }
                    if (head.State == AgentReplyTaskState.Failed)
                    {
                        if (head.RetryExhausted) return await TaskRejectionAsync("retry_exhausted", head, token);
                        if (head.NextRetryAtUtc is not DateTimeOffset due || now < due) return await TaskRejectionAsync("retry_pending", head, token);
                    }
                    AgentContextDto nextContext;
                    try { nextContext = await contextBuilder.BuildTaskAsync(connection.MeetingId, head.CandidateFromSequenceExclusive, head.TriggerSequence, connection.DeclaredContextCharacterLimit, token); }
                    catch (ArgumentException)
                    {
                        head.StopForContextFailure(now);
                        RecordVisibleChange(cursor, now);
                        unitOfWork.RegisterEdit(head);
                        return await TaskRejectionAsync("retry_exhausted", head, token);
                    }
                    if (!renewed) { ExtendLease(connection, now, seconds); renewed = true; }
                    var replayReason = attempt is null ? null : attempt.InvalidationReason == "connection_taken_over" ? "takeover"
                        : attempt.InvalidationReason is "connection_resumed" or "transport_closed" or "connection_lease_expired" or "server_restart" ? "recovery" : "retry";
                    var nextAttempt = AgentTaskDeliveryAttempt.CreateReliable(head, connection, Window(nextContext), now, options.ContextConfirmationTimeoutSeconds);
                    head.DeliverReliable(nextAttempt, attempt, now);
                    unitOfWork.RegisterAdd(nextAttempt);
                    if (attempt is not null) unitOfWork.RegisterEdit(attempt);
                    unitOfWork.RegisterEdit(head);
                    RecordVisibleChange(cursor, now);
                    return Mention(head, nextAttempt, nextContext, false, replayReason);
                }, cancellationToken);
                if (!result.Succeeded) return result;
                if (result.Data is AgentMentionResultDto mention)
                    return await RunReliableAsync(request.ConnectionSessionId, transportSession, async (connection, cursor, token) =>
                    {
                        var task = await agentRepository.FindTaskForAgentAsync(connection.MeetingId, connection.ParticipantId, mention.TaskId, token);
                        var attempt = task is null ? null : await agentRepository.FindDeliveryAttemptAsync(connection.MeetingId, task.ID, mention.DeliveryAttemptId, token);
                        if (task is null || attempt is null || !IsCurrentAttempt(connection, task, attempt)) return ReliableFailure("delivery_attempt_stale", "get_agent_status");
                        if (task.State == AgentReplyTaskState.Processing) return await TaskRejectionAsync("task_in_progress", task, token);
                        if (task.State != AgentReplyTaskState.Delivered) return ReliableFailure("delivery_attempt_stale", "get_agent_status");
                        var now = UtcNow();
                        if (attempt.ConfirmationDeadlineUtc <= now)
                        {
                            FailCurrentAttempt(task, attempt, cursor, AgentTaskFailureCode.ConfirmationTimeout, now);
                            return await TaskRejectionAsync(task.RetryExhausted ? "retry_exhausted" : "retry_pending", task, token);
                        }
                        return result;
                    }, cancellationToken);
                if (stopwatch.Elapsed >= timeout) return result;
                try { await observation; }
                catch (OperationCanceledException) when (bindingCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { }
            }
            finally
            {
                await observationCancellation.CancelAsync();
                try { await observation; }
                catch (OperationCanceledException) when (observationCancellation.IsCancellationRequested) { }
            }
        }
    }

    private void FailCurrentAttempt(AgentReplyTask task, AgentTaskDeliveryAttempt? attempt, AgentContextCursor cursor,
        AgentTaskFailureCode code, DateTimeOffset now)
    {
        if (!task.FailReliable(code, new(options.MaximumAutomaticRetries, options.RetryBackoffSeconds), now)) return;
        if (attempt is not null && attempt.Invalidate(code, now)) unitOfWork.RegisterEdit(attempt);
        unitOfWork.RegisterEdit(task);
        RecordVisibleChange(cursor, now);
    }

    private async Task<AgentOperationResult> TaskRejectionAsync(string code, AgentReplyTask task, CancellationToken cancellationToken)
    {
        var summary = await ReliableSummaryAsync(task, cancellationToken);
        return AgentOperationResult.Failure(code, "The queue head cannot be delivered now.", new AgentRecoveryErrorData(summary.NextAction!)
        { TaskSummary = summary, NextRetryAt = task.NextRetryAtUtc, RetryExhausted = task.RetryExhausted });
    }

    private static AgentOperationResult Mention(AgentReplyTask task, AgentTaskDeliveryAttempt attempt, AgentContextDto context, bool replayed, string? reason) =>
        AgentOperationResult.Success(new AgentMentionResultDto("mention", task.ID, "delivered", task.TriggerMessageId, task.TriggerSequence,
            task.TriggerMentionId, task.SourceParticipantId, attempt.ID, task.ReplyIdempotencyKey, context, AgentReliabilityProtocol.ConfirmTool)
        { ConfirmationDeadline = attempt.ConfirmationDeadlineUtc, Replayed = replayed, ReplayReason = reason, ContextPurpose = "task" }, "An Agent mention is ready for confirmation.");

    private static AgentContextWindow AttemptWindow(AgentTaskDeliveryAttempt attempt) => new(attempt.CandidateLastSequence,
        attempt.CandidateFirstSequence, attempt.CandidateLastSequence, attempt.DeliveredFirstSequence, attempt.DeliveredLastSequence,
        attempt.DeclaredContextCharacterLimit, attempt.DeliveredCharacterCount, attempt.Truncated, attempt.OverLimit,
        attempt.TruncatedMessageCount, attempt.ContextFormatVersion);
}
