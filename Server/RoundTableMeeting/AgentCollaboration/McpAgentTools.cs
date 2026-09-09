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
public sealed class McpAgentTools(IAgentCollaborationService collaboration, McpTransportSessionAccessor transportSession, IHttpContextAccessor httpContextAccessor, ILogger<McpAgentTools> logger)
{
	[McpServerTool(Name = "join_meeting", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
	[Description("Join an existing meeting as an AI participant.")]
	public Task<AgentOperationResult> JoinMeetingAsync([Description("Meeting code supplied by the meeting owner.")] string? meetingCode, [Description("Display name for the AI participant.")] string? displayName, [Description("Maximum context character limit requested by the AI.")] int contextCharacterLimit, McpServer server, CancellationToken cancellationToken)
	{
		Bind(server);
		return ExecuteAsync(() => collaboration.JoinAsync(new AgentJoinRequest(meetingCode, displayName, contextCharacterLimit, httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString()), transportSession.Current, cancellationToken));
	}

	[McpServerTool(Name = "wait_for_mention", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
	[Description("Wait for the next meeting message that explicitly mentions this AI participant.")]
	public Task<AgentOperationResult> WaitForMentionAsync([Description("Business connection session identifier returned by join_meeting.")] Guid? connectionSessionId, McpServer server, CancellationToken cancellationToken)
	{
		Bind(server);
		return ExecuteAsync(() => collaboration.WaitAsync(new AgentWaitRequest(connectionSessionId), transportSession.Current, cancellationToken));
	}

	[McpServerTool(Name = "reply_to_mention", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
	[Description("Reply to the currently delivered mention task.")]
	public Task<AgentOperationResult> ReplyToMentionAsync([Description("Business connection session identifier returned by join_meeting.")] Guid? connectionSessionId, [Description("Mention task identifier.")] Guid? taskId, [Description("Delivery attempt identifier.")] Guid? deliveryAttemptId, [Description("Server-issued reply idempotency identifier.")] Guid? replyIdempotencyKey, [Description("Reply text.")] string? content, McpServer server, CancellationToken cancellationToken)
	{
		Bind(server);
		return ExecuteAsync(() => collaboration.ReplyAsync(new AgentReplyRequest(connectionSessionId, taskId, deliveryAttemptId, replyIdempotencyKey, content), transportSession.Current, cancellationToken));
	}

	[McpServerTool(Name = "get_agent_status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
	[Description("Read the state of the current AI meeting connection.")]
	public Task<AgentOperationResult> GetAgentStatusAsync([Description("Business connection session identifier returned by join_meeting.")] Guid? connectionSessionId, McpServer server, CancellationToken cancellationToken)
	{
		Bind(server);
		return ExecuteAsync(() => collaboration.GetStatusAsync(new AgentStatusRequest(connectionSessionId), transportSession.Current, cancellationToken));
	}

    private async Task<AgentOperationResult> ExecuteAsync(Func<Task<AgentOperationResult>> operation)
    {
        try { return await operation(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            logger.LogError("Agent operation failed. ExceptionType: {ExceptionType}", exception.GetType().Name);
            return AgentOperationResult.Failure("agent_operation_unavailable", "The Agent operation is temporarily unavailable.");
        }
    }

	private void Bind(McpServer server)
	{
		transportSession.Bind(server);
	}
}
