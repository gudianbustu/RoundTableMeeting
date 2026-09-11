using System.Security.Cryptography;
using System.Text;
using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.AgentCollaboration;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.Application.AgentCollaboration;

public sealed partial class AgentCollaborationService
{
    public Task<AgentOperationResult> ReportFailureReliableAsync(AgentFailureRequest request, McpTransportSessionKey? transportSession,
        CancellationToken cancellationToken = default)
    {
        if (!AgentTaskFailurePolicy.TryParseReportedCode(request.FailureCode, out var code)) return Task.FromResult(ReliableFailure("failure_code_invalid"));
        if (request.TaskId is null || request.TaskId == Guid.Empty || request.DeliveryAttemptId is null || request.DeliveryAttemptId == Guid.Empty)
            return Task.FromResult(ReliableFailure("agent_request_invalid"));
        return RunReliableAsync(request.ConnectionSessionId, transportSession, async (connection, cursor, token) =>
        {
            var task = await agentRepository.FindTaskForAgentAsync(connection.MeetingId, connection.ParticipantId, request.TaskId.Value, token);
            if (task is null) return ReliableFailure("task_not_available");
            var attempt = await agentRepository.FindDeliveryAttemptAsync(connection.MeetingId, task.ID, request.DeliveryAttemptId.Value, token);
            if (attempt is null) return ReliableFailure("delivery_attempt_invalid");
            if (attempt.FailureDecision is { } saved)
            {
                if (attempt.ReportedFailureCode != request.FailureCode) return ReliableFailure("operation_conflict");
                return AgentOperationResult.Success(new AgentFailureResultDto(task.ID, saved.RetryCycle, task.RetryCycle, true,
                    saved.RetryExhausted, AgentTaskFailurePolicy.ToWire(saved.StopReason), saved.NextRetryAtUtc, "get_agent_status"), "The failure decision was replayed.");
            }
            if (!IsCurrentAttempt(connection, task, attempt)) return ReliableFailure("delivery_attempt_stale", "get_agent_status");
            if (task.State != AgentReplyTaskState.Processing || attempt.State != AgentTaskDeliveryAttemptState.Confirmed) return ReliableFailure("task_not_available");
            var now = UtcNow();
            var decision = new AgentTaskFailurePolicy(options.MaximumAutomaticRetries, options.RetryBackoffSeconds).Decide(code, task.AutomaticRetryCount, now);
            ExtendLease(connection, now);
            attempt.RecordReportedFailure(code, decision);
            FailCurrentAttempt(task, attempt, cursor, code, now);
            return AgentOperationResult.Success(new AgentFailureResultDto(task.ID, task.RetryCycle, task.RetryCycle, false,
                task.RetryExhausted, AgentTaskFailurePolicy.ToWire(task.StopReason), task.NextRetryAtUtc,
                task.RetryExhausted ? "await_explicit_retry" : AgentCollaborationContract.WaitToolName), "The failure was recorded.");
        }, cancellationToken);
    }

    public async Task<AgentOperationResult> RetryReliableAsync(AgentRetryRequest request, McpTransportSessionKey? transportSession,
        CancellationToken cancellationToken = default)
    {
        var invalid = AgentRecoveryRequestValidator.ValidateRetry(request);
        if (invalid is not null) return invalid;
        var result = await RunReliableAsync(request.ConnectionSessionId, transportSession, async (connection, cursor, token) =>
        {
            var task = await agentRepository.FindTaskForAgentAsync(connection.MeetingId, connection.ParticipantId, request.TaskId!.Value, token);
            if (task is null) return ReliableFailure("task_not_available");
            var receipt = await recoveryRepository.FindRetryOperationAsync(connection.MeetingId, connection.ParticipantId, task.ID, request.OperationId!.Value, token);
            if (receipt is not null) return AgentOperationResult.Success(new AgentRetryResultDto(task.ID, receipt.ResultRetryCycle!.Value,
                task.RetryCycle, true, task.RetryExhausted, AgentTaskFailurePolicy.ToWire(task.StopReason), task.NextRetryAtUtc, "get_agent_status"), "The retry operation was replayed.");
            var head = (await agentRepository.ListTasksAsync(connection.MeetingId, connection.ParticipantId, token))
                .FirstOrDefault(item => item.State is not (AgentReplyTaskState.Replied or AgentReplyTaskState.Cancelled));
            if (head?.ID != task.ID) return ReliableFailure("task_not_available");
            if (task.State == AgentReplyTaskState.Processing) return await TaskRejectionAsync("task_in_progress", task, token);
            if (task.State != AgentReplyTaskState.Failed || !task.RetryExhausted) return ReliableFailure("retry_not_required", "get_agent_status");
            var now = UtcNow();
            ExtendLease(connection, now);
            task.BeginExplicitRetry(now);
            unitOfWork.RegisterEdit(task);
            RecordVisibleChange(cursor, now);
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(task.ID.ToString("D"))));
            unitOfWork.RegisterAdd(AgentRecoveryOperation.ForRetry(connection.MeetingId, connection.ParticipantId, task.ID,
                request.OperationId.Value, fingerprint, task.RetryCycle, now));
            return AgentOperationResult.Success(new AgentRetryResultDto(task.ID, task.RetryCycle, task.RetryCycle, false,
                false, null, task.NextRetryAtUtc, AgentCollaborationContract.WaitToolName), "A new retry cycle was scheduled.");
        }, cancellationToken);
        if (result.Data is AgentRetryResultDto { Replayed: false } && TryResolveRegistration(request.ConnectionSessionId, transportSession, out var registration))
            taskSignals.Signal(registration.MeetingId, registration.ParticipantId);
        return result;
    }

    public async Task<AgentOperationResult> ReplyReliableAsync(AgentReplyRequest request, McpTransportSessionKey? transportSession,
        CancellationToken cancellationToken = default)
    {
        if (request.TaskId is null || request.TaskId == Guid.Empty) return ReliableFailure("task_not_available");
        if (request.DeliveryAttemptId is null || request.DeliveryAttemptId == Guid.Empty) return ReliableFailure("delivery_attempt_invalid");
        if (request.ReplyIdempotencyKey is null || request.ReplyIdempotencyKey == Guid.Empty) return ReliableFailure("reply_idempotency_mismatch");
        var result = await RunReliableAsync(request.ConnectionSessionId, transportSession, async (connection, cursor, token) =>
        {
            string content;
            try { content = MeetingMessage.NormalizeContent(request.Content); MeetingMessage.ValidateContentLength(content, options.ReplyCharacterLimit); }
            catch (ArgumentException) { return ReliableFailure("message_validation_failed"); }
            var task = await agentRepository.FindTaskForAgentAsync(connection.MeetingId, connection.ParticipantId, request.TaskId.Value, token);
            if (task is null) return ReliableFailure("task_not_available");
            if (task.ReplyIdempotencyKey != request.ReplyIdempotencyKey) return ReliableFailure("reply_idempotency_mismatch");
            var attempt = await agentRepository.FindDeliveryAttemptAsync(connection.MeetingId, task.ID, request.DeliveryAttemptId.Value, token);
            if (attempt is null) return ReliableFailure("delivery_attempt_invalid");
            if (task.State == AgentReplyTaskState.Replied)
            {
                if (task.CurrentAttemptId != attempt.ID || task.ReplyMessageId is not Guid messageId) return ReliableFailure("delivery_attempt_stale", "get_agent_status");
                var existing = await messages.FindByIdAsync(connection.MeetingId, messageId, token);
                if (existing is null || existing.AuthorParticipantId != connection.ParticipantId || existing.AuthorKind != MessageAuthorKind.Agent
                    || existing.ClientSubmissionId != task.ReplyIdempotencyKey || existing.Content != content) return ReliableFailure("reply_idempotency_mismatch");
                return AgentOperationResult.Success(new AgentReplyResultDto(await ToMessageDtoAsync(ToAppendResult(existing), token), true,
                    AgentCollaborationContract.WaitToolName) { Kind = "replied" }, "The accepted reply was replayed.");
            }
            if (!IsCurrentAttempt(connection, task, attempt)) return ReliableFailure("delivery_attempt_stale", "get_agent_status");
            if (attempt.State != AgentTaskDeliveryAttemptState.Confirmed) return ReliableFailure("context_confirmation_required", AgentReliabilityProtocol.ConfirmTool);
            if (task.State != AgentReplyTaskState.Processing) return ReliableFailure("task_not_available");
            var now = UtcNow();
            ExtendLease(connection, now);
            var agent = (await participants.ListAsync(connection.MeetingId, token)).Single(item => item.ID == connection.ParticipantId);
            var appended = await messageAppendCore.AppendAgentAsync(connection.MeetingId, connection.ParticipantId, agent.DisplayName, content, task.ReplyIdempotencyKey, token);
            task.MarkReplied(appended.MessageId, now);
            unitOfWork.RegisterEdit(task);
            RecordVisibleChange(cursor, now);
            // Flush inside the existing transaction so the response includes the newly parsed mentions.
            // Any failure, including response projection, still rolls back the message, task and outbox together.
            await unitOfWork.CommitAsync(token);
            return AgentOperationResult.Success(new AgentReplyResultDto(await ToMessageDtoAsync(appended, token), false,
                AgentCollaborationContract.WaitToolName) { Kind = "replied" }, "The Agent reply was accepted.");
        }, cancellationToken);
        if (result.Data is AgentReplyResultDto { Replayed: false } && TryResolveRegistration(request.ConnectionSessionId, transportSession, out var registration))
            taskSignals.Signal(registration.MeetingId, registration.ParticipantId);
        return result;
    }

    public Task<AgentOperationResult> StatusReliableAsync(AgentStatusRequest request, McpTransportSessionKey? transportSession,
        CancellationToken cancellationToken = default) => RunReliableAsync(request.ConnectionSessionId, transportSession, async (connection, cursor, token) =>
    {
        var initial = await agentRepository.FindInitialContextAsync(connection.MeetingId, connection.ParticipantId, connection.ID, token);
        if (initial is null) return ReliableFailure("agent_operation_unavailable");
        var tasks = await agentRepository.ListTasksAsync(connection.MeetingId, connection.ParticipantId, token);
        var head = tasks.FirstOrDefault(task => task.State is not (AgentReplyTaskState.Replied or AgentReplyTaskState.Cancelled));
        var requested = request.TaskId is Guid id ? tasks.SingleOrDefault(task => task.ID == id) : null;
        if (request.TaskId is not null && requested is null) return ReliableFailure("task_not_available");
        var headSummary = head is null ? null : await ReliableSummaryAsync(head, token);
        var requestedSummary = requested is null ? null : await ReliableSummaryAsync(requested, token);
        var counts = Enum.GetValues<AgentReplyTaskState>().ToDictionary(state => ToWire(state), state => tasks.Count(task => task.State == state), StringComparer.Ordinal);
        return AgentOperationResult.Success(new AgentStatusResultDto(connection.MeetingId, connection.ParticipantId, ReliableConnectionDto(connection, initial),
            counts, headSummary, cursor.DeliveredThroughSequence, cursor.AllocatedThroughSequence)
        {
            Kind = "agent_status", InitialDeliveryId = initial.ID, RequestedTask = requestedSummary,
            NextAction = initial.State == InitialContextState.Prepared ? AgentReliabilityProtocol.ConfirmTool : headSummary?.NextAction ?? AgentCollaborationContract.WaitToolName
        }, "The Agent status was read.");
    }, cancellationToken);
}
