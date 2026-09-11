using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;
using RoundTableMeeting.Abstraction.Services;

namespace RoundTableMeeting.AgentCollaboration;

[McpServerToolType]
public sealed class McpAgentTools(IAgentCollaborationService collaboration, McpTransportSessionAccessor transportSession, IHttpContextAccessor httpContextAccessor, ILogger<McpAgentTools> logger,
    IAgentConnectionRegistry connectionRegistry)
{
	[McpServerTool(Name = "join_meeting", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
	[Description("Join an existing meeting as an AI participant.")]
	public Task<AgentOperationResult> JoinMeetingAsync([Description("Meeting code supplied by the meeting owner.")] string? meetingCode, [Description("Display name for the AI participant.")] string? displayName, [Description("Maximum context character limit requested by the AI.")] int contextCharacterLimit, McpServer server, CancellationToken cancellationToken,
        [Description("legacy_v1 (default) or explicit_ack_v1 for explicit confirmation and recovery.")] string? reliabilityMode = null,
        [Description("create (default) or takeover to explicitly replace the current Agent, including an online Agent.")] string? joinMode = null,
        [Description("Client-generated operation UUID, persisted and reused only for the same reliable join.")] Guid? operationId = null,
        [Description("Client-owned 32-byte random secret encoded as canonical unpadded base64url; persist privately before joining.")] string? recoveryKey = null)
	{
		Bind(server);
		return ExecuteAsync(() => collaboration.JoinAsync(new AgentJoinRequest(meetingCode, displayName, contextCharacterLimit, httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString())
        { ReliabilityMode = reliabilityMode, JoinMode = joinMode, OperationId = operationId, RecoveryKey = recoveryKey }, transportSession.Current, cancellationToken),
            reliable: reliabilityMode == AgentReliabilityProtocol.ExplicitAck);
	}

	[McpServerTool(Name = "wait_for_mention", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
	[Description("Wait for the next meeting message that explicitly mentions this AI participant.")]
	public Task<AgentOperationResult> WaitForMentionAsync([Description("Business connection session identifier returned by join_meeting.")] Guid? connectionSessionId, McpServer server, CancellationToken cancellationToken,
        [Description("Reliable mode only: bounded server wait in seconds; omitted uses the negotiated default.")] int? waitTimeoutSeconds = null)
	{
		Bind(server);
		return ExecuteAsync(() => collaboration.WaitAsync(new AgentWaitRequest(connectionSessionId) { WaitTimeoutSeconds = waitTimeoutSeconds }, transportSession.Current, cancellationToken), connectionId: connectionSessionId);
	}

	[McpServerTool(Name = "reply_to_mention", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
	[Description("Reply to the currently delivered mention task.")]
	public Task<AgentOperationResult> ReplyToMentionAsync([Description("Business connection session identifier returned by join_meeting.")] Guid? connectionSessionId, [Description("Mention task identifier.")] Guid? taskId, [Description("Delivery attempt identifier.")] Guid? deliveryAttemptId, [Description("Server-issued reply idempotency identifier.")] Guid? replyIdempotencyKey, [Description("Reply text.")] string? content, McpServer server, CancellationToken cancellationToken)
	{
		Bind(server);
		return ExecuteAsync(() => collaboration.ReplyAsync(new AgentReplyRequest(connectionSessionId, taskId, deliveryAttemptId, replyIdempotencyKey, content), transportSession.Current, cancellationToken), connectionId: connectionSessionId);
	}

	[McpServerTool(Name = "get_agent_status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
	[Description("Read the state of the current AI meeting connection.")]
	public Task<AgentOperationResult> GetAgentStatusAsync([Description("Business connection session identifier returned by join_meeting.")] Guid? connectionSessionId, McpServer server, CancellationToken cancellationToken,
        [Description("Reliable mode only: optionally inspect a specific task owned by this Agent.")] Guid? taskId = null)
	{
		Bind(server);
		return ExecuteAsync(() => collaboration.GetStatusAsync(new AgentStatusRequest(connectionSessionId) { TaskId = taskId }, transportSession.Current, cancellationToken), connectionId: connectionSessionId);
	}

    [McpServerTool(Name = "resume_agent", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Resume a reliable Agent on this MCP session using its private recovery key and a persisted operation UUID.")]
    public Task<AgentOperationResult> ResumeAgentAsync(Guid? connectionSessionId, string? recoveryKey, Guid? operationId, McpServer server, CancellationToken cancellationToken)
    {
        Bind(server);
        return ExecuteAsync(() => collaboration.ResumeReliableAsync(new(connectionSessionId, recoveryKey, operationId), transportSession.Current,
            httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString(), cancellationToken), reliable: true, connectionId: connectionSessionId);
    }

    [McpServerTool(Name = "confirm_context", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Confirm context only after consuming it: initial requires initialDeliveryId; task requires taskId and deliveryAttemptId. Do not mix targets.")]
    public Task<AgentOperationResult> ConfirmContextAsync(Guid? connectionSessionId, string? kind, McpServer server, CancellationToken cancellationToken,
        Guid? initialDeliveryId = null, Guid? taskId = null, Guid? deliveryAttemptId = null)
    {
        Bind(server);
        return ExecuteAsync(() => collaboration.ConfirmReliableAsync(new(connectionSessionId, kind, initialDeliveryId, taskId, deliveryAttemptId), transportSession.Current, cancellationToken), reliable: true, connectionId: connectionSessionId);
    }

    [McpServerTool(Name = "renew_agent_lease", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Renew the current reliable Agent lease independently of model generation or waiting. An expired lease requires resume_agent.")]
    public Task<AgentOperationResult> RenewAgentLeaseAsync(Guid? connectionSessionId, McpServer server, CancellationToken cancellationToken)
    {
        Bind(server);
        return ExecuteAsync(() => collaboration.RenewReliableAsync(new(connectionSessionId), transportSession.Current, cancellationToken), reliable: true, connectionId: connectionSessionId);
    }

    [McpServerTool(Name = "report_task_failure", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Report a confirmed task failure: agent_transient_failure permits bounded automatic retry; agent_permanent_failure blocks the FIFO head until explicit retry.")]
    public Task<AgentOperationResult> ReportTaskFailureAsync(Guid? connectionSessionId, Guid? taskId, Guid? deliveryAttemptId, string? failureCode, McpServer server, CancellationToken cancellationToken)
    {
        Bind(server);
        return ExecuteAsync(() => collaboration.ReportFailureReliableAsync(new(connectionSessionId, taskId, deliveryAttemptId, failureCode), transportSession.Current, cancellationToken), reliable: true, connectionId: connectionSessionId);
    }

    [McpServerTool(Name = "retry_task", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Explicitly start a new retry cycle for the exhausted FIFO head. Persist and reuse the operation UUID for retries of this request.")]
    public Task<AgentOperationResult> RetryTaskAsync(Guid? connectionSessionId, Guid? taskId, Guid? operationId, McpServer server, CancellationToken cancellationToken)
    {
        Bind(server);
        return ExecuteAsync(() => collaboration.RetryReliableAsync(new(connectionSessionId, taskId, operationId), transportSession.Current, cancellationToken), reliable: true, connectionId: connectionSessionId);
    }

    private async Task<AgentOperationResult> ExecuteAsync(Func<Task<AgentOperationResult>> operation,
        bool reliable = false, Guid? connectionId = null)
    {
        // Capture the negotiated format before an operation can retire the binding. Never
        // infer an existing connection's mode from optional request fields or another ID.
        var knownBinding = false;
        if (connectionId is Guid id && transportSession.Current is { } transport
            && connectionRegistry.TryGetRegistration(transport, out var registration)
            && registration.ConnectionSessionId == id)
        {
            knownBinding = true;
            reliable = registration.ReliabilityMode == AgentReliabilityProtocol.ExplicitAck;
        }
        try { return await operation(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            logger.LogError("Agent operation failed. ExceptionType: {ExceptionType}", exception.GetType().Name);
            return reliable
                ? AgentOperationResult.Failure("agent_operation_unavailable", "The Agent operation is temporarily unavailable.",
                    new AgentRecoveryErrorData(knownBinding ? "get_agent_status" : "stop"))
                : AgentOperationResult.Failure("agent_operation_unavailable", "The Agent operation is temporarily unavailable.");
        }
    }

	private void Bind(McpServer server)
	{
		transportSession.Bind(server);
	}
}
