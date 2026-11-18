using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Abstraction.Contracts.Meetings;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Entities;

namespace RoundTableMeeting.AgentCollaboration;

[McpServerToolType]
public sealed class CurrentMcpTools(Domain.Repositories.IMeetingDiscussionRepository discussion, CurrentMcpSessionService sessions, IMessageHistoryService history,
    IMessageAppendService messages, IMessageLookupService lookup, IAttachmentService attachments,
    IMeetingLifecycleService lifecycle, IServiceScopeFactory scopes,
    IHttpContextAccessor http, ILogger<CurrentMcpTools> logger, McpTransferTickets tickets)
{
    private static McpTransportSessionKey Transport(McpServer server) => McpTransportSessionAccessor.FromSessionId(server.SessionId)
        ?? throw new McpOperationException("transport_required");
    private async Task<McpResult<T>> Safe<T>(Func<Task<T>> action)
    {
        try { return McpResult<T>.Ok(await action()); }
        catch (McpOperationException e) { return McpResult<T>.Fail(e.Message); }
        catch (AttachmentOperationException e) { return McpResult<T>.Fail(e.Code); }
        catch (ArgumentException) { return McpResult<T>.Fail("validation_failed"); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { logger.LogError("Current MCP operation failed: {ExceptionType}", e.GetType().Name); return McpResult<T>.Fail("operation_unavailable"); }
    }
    private Task<McpResult<T>> Run<T>(Guid id, McpServer server, CancellationToken ct, Func<AgentConnectionSession, CancellationToken, Task<T>> action)
        => Safe(() => sessions.ExecuteAsync(Transport(server), id, action, ct));
    private static T Data<T>(MessageOperationResult<T> result) => result.Status is MessageOperationStatus.Success or MessageOperationStatus.Created or MessageOperationStatus.Replayed
        ? result.Data! : throw new McpOperationException(System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(result.Status.ToString()));

    [McpServerTool(Name = "join_meeting", UseStructuredContent = true)]
    [Description("Join using the meeting invitation code. create rejects existing names; reconnect restores an offline identity; takeover explicitly replaces an online connection. Save returned IDs and lifecycleProof. Start read_messages at afterSequence=0. No task/ACK protocol.")]
    public Task<McpResult<McpConnection>> Join(string meetingCode, string displayName, McpServer server, CancellationToken cancellationToken, string joinMode = "create")
        => Safe(() => sessions.JoinAsync(Transport(server), meetingCode, displayName, joinMode, http.HttpContext?.Connection.RemoteIpAddress?.ToString(), cancellationToken));

    [McpServerTool(Name = "get_meeting_info", ReadOnly = true, UseStructuredContent = true)]
    [Description("Read meeting details, participant IDs, membershipState and connectionState (online/offline) for humans and agents. Connection state is a communication snapshot, not evidence of reading or model activity.")]
    public Task<McpResult<McpMeetingInfo>> Info(Guid connectionSessionId, McpServer server, CancellationToken cancellationToken)
        => Run(connectionSessionId, server, cancellationToken, sessions.InfoAsync);

    [McpServerTool(Name = "read_messages", ReadOnly = true, UseStructuredContent = true)]
    [Description("Read only this participant's visible messages. Save nextCursor exactly, including contentOffset for partial messages. Positions are participant-local. Character budget counts Unicode scalars; mention offsets remain UTF-16 positions in the full original message. Never respond before a message is complete.")]
    public Task<McpResult<McpMessages>> Read(Guid connectionSessionId, McpServer server, CancellationToken cancellationToken,
        long afterSequence = 0, int contentOffset = 0, int characterBudget = 100000, bool mentionsOnly = false)
        => Run(connectionSessionId, server, cancellationToken, (c, ct) => ReadCore(c, afterSequence, contentOffset, characterBudget, mentionsOnly, ct));

    internal async Task<McpMessages> ReadCore(AgentConnectionSession c, long after, int offset, int budget, bool mentionsOnly, CancellationToken ct)
    {
        if (after < 0 || offset < 0 || budget is < 1 or > 100000) throw new McpOperationException("validation_failed");
        if (mentionsOnly) after = Math.Max(after, await discussion.InboxStartAsync(c.MeetingId, c.ParticipantId, ct));
        var page = Data(await history.GetPageAsync(c.MeetingId, new(AfterSequence: after, Limit: 100), ct));
        var selected = new List<McpMessageFragment>(); var cursor = new McpReadCursor(after, offset); var remaining = budget;
        foreach (var message in page.Items)
        {
            if (mentionsOnly && !message.IsForAgent(c.ParticipantId))
            { if (cursor.ContentOffset != 0 && !message.DiscussionInterrupted) throw new McpOperationException("invalid_content_offset"); cursor = new(message.Sequence); continue; }
            var scalars = message.Content.EnumerateRunes().ToArray();
            var start = cursor.ContentOffset;
            if (start > scalars.Length) throw new McpOperationException("invalid_content_offset");
            if (remaining == 0 && scalars.Length > start) break;
            var count = Math.Min(remaining, scalars.Length - start);
            var text = new StringBuilder(); for (var i = start; i < start + count; i++) text.Append(scalars[i].ToString());
            var complete = start + count == scalars.Length;
            selected.Add(new(message with { Content = text.ToString() }, start, scalars.Length, complete) { DiscussionTopicId = message.DiscussionTopicId });
            remaining -= count;
            cursor = complete ? new(message.Sequence) : new(cursor.AfterSequence, start + count);
            if (!complete) break;
        }
        if (page.Items.Count == 0 && offset != 0) throw new McpOperationException("invalid_content_offset");
        var more = cursor.ContentOffset != 0 || page.HasMoreAfter || page.Items.Any(m => m.Sequence > cursor.AfterSequence);
        return new(selected, cursor, page.VisibleThroughSequence, more, selected.Count == 0 ? "idle" : "messages");
    }

    [McpServerTool(Name = "wait_messages", UseStructuredContent = true)]
    [Description("Bounded wait for explicit public mentions or incoming private messages by default. idle means keep waiting, not leave. Pass and persist nextCursor, including offset. Each wait renews the five-minute connection lease. Use renew_agent_lease during longer work. Messages with optionalAgentResponse=true invite a voluntary reply; skip when you have nothing to add.")]
    public Task<McpResult<McpMessages>> Wait(Guid connectionSessionId, McpServer server, CancellationToken cancellationToken,
        long afterSequence = 0, int contentOffset = 0, int characterBudget = 100000, int timeoutSeconds = 25, bool mentionsOnly = true)
        => Safe(async () =>
        {
            if (timeoutSeconds is < 1 or > 60) throw new McpOperationException("validation_failed");
            var transport = Transport(server); var until = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
            await sessions.ExecuteAsync(transport, connectionSessionId, sessions.RenewAsync, cancellationToken);
            var cursor = new McpReadCursor(afterSequence, contentOffset);
            while (true)
            {
                await using var scope = scopes.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<CurrentMcpSessionService>();
                var tools = ActivatorUtilities.CreateInstance<CurrentMcpTools>(scope.ServiceProvider);
                var result = await service.ExecuteAsync(transport, connectionSessionId,
                    (c, ct) => tools.ReadCore(c, cursor.AfterSequence, cursor.ContentOffset, characterBudget, mentionsOnly, ct), cancellationToken);
                if (mentionsOnly && result.Items.FirstOrDefault() is { Message.OptionalAgentResponse: true } first
                    && first.DiscussionTopicId.HasValue && first.DiscussionTopicId != first.Message.MessageId)
                {
                    var now = DateTimeOffset.UtcNow;
                    var deadline = first.Message.CreatedAt.AddMilliseconds(750);
                    var delay = (deadline < until ? deadline : until) - now;
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, cancellationToken);
                        return await service.ExecuteAsync(transport, connectionSessionId,
                            (c, ct) => tools.ReadCore(c, cursor.AfterSequence, cursor.ContentOffset, characterBudget, mentionsOnly, ct), cancellationToken);
                    }
                }
                if (result.Items.Count > 0 || DateTimeOffset.UtcNow >= until) return result;
                cursor = result.NextCursor;
                await Task.Delay(300, cancellationToken);
            }
        });

    [McpServerTool(Name = "send_message", Idempotent = true, UseStructuredContent = true)]
    [Description("Send public or private text, attachments, or reply. Persist a new clientSubmissionId BEFORE calling; retry the exact payload with that same UUID after uncertain outcomes. Set replyToMessageId to the message that prompted your reply. After a free discussion is interrupted this is required for ordinary unaddressed public Agent messages. On discussion_ended discard the draft and resume waiting. visibility is public or private; private requires recipientParticipantId. MentionCandidates use explicit participant IDs and UTF-16 text offsets; plain @ text alone is not a mention.")]
    public Task<McpResult<MessageDto>> Send(Guid connectionSessionId, SubmitMessageRequest request, McpServer server, CancellationToken cancellationToken)
        => Run(connectionSessionId, server, cancellationToken, async (c, ct) => Data(await messages.AppendHumanAsync(c.MeetingId, request, ct)));

    [McpServerTool(Name = "get_message", ReadOnly = true, UseStructuredContent = true)]
    [Description("Read one visible message by ID. Invisible private messages return message_not_found.")]
    public Task<McpResult<MessageDto>> GetMessage(Guid connectionSessionId, Guid messageId, McpServer server, CancellationToken cancellationToken)
        => Run(connectionSessionId, server, cancellationToken, async (c, ct) => Data(await lookup.GetMessageAsync(c.MeetingId, messageId, ct)));

    [McpServerTool(Name = "get_submission", ReadOnly = true, UseStructuredContent = true)]
    [Description("Resolve an uncertain send using your persisted clientSubmissionId.")]
    public Task<McpResult<SubmissionStatusDto>> Submission(Guid connectionSessionId, Guid clientSubmissionId, McpServer server, CancellationToken cancellationToken)
        => Run(connectionSessionId, server, cancellationToken, async (c, ct) => Data(await lookup.GetSubmissionAsync(c.MeetingId, clientSubmissionId, ct)));

    [McpServerTool(Name = "list_attachments", ReadOnly = true, UseStructuredContent = true)]
    [Description("List visible shared attachment metadata; scope all/public/private. Use prepare_attachment_transfer to transfer bytes.")]
    public Task<McpResult<AttachmentPageDto>> ListAttachments(Guid connectionSessionId, McpServer server, CancellationToken cancellationToken, long? beforeSequence = null, string scope = "all", Guid? peerParticipantId = null)
        => Run(connectionSessionId, server, cancellationToken, (c, ct) => attachments.ListAsync(c.MeetingId, new(BeforeSequence: beforeSequence, Limit: 50, Scope: scope, PeerParticipantId: peerParticipantId), ct));

    [McpServerTool(Name = "renew_agent_lease", UseStructuredContent = true)]
    [Description("Renew the connection for five minutes. Expired/retired connections require a NEW MCP transport and explicit reconnect or takeover.")]
    public Task<McpResult<McpConnection>> Renew(Guid connectionSessionId, McpServer server, CancellationToken cancellationToken)
        => Run(connectionSessionId, server, cancellationToken, sessions.RenewAsync);

    [McpServerTool(Name = "disconnect_agent", UseStructuredContent = true)]
    [Description("Disconnect this transport while retaining meeting membership. A subsequent reconnect requires a new MCP transport.")]
    public Task<McpResult<McpAcknowledgement>> Disconnect(Guid connectionSessionId, McpServer server, CancellationToken cancellationToken)
        => Run(connectionSessionId, server, cancellationToken, sessions.DisconnectAsync);

    [McpServerTool(Name = "leave_meeting", UseStructuredContent = true)]
    [Description("Leave ONLY when the user explicitly asks. Ends membership and retires this connection. Persist operationId for this leave.")]
    public Task<McpResult<MeetingMembershipDto>> Leave(Guid connectionSessionId, Guid operationId, McpServer server, CancellationToken cancellationToken)
        => Safe(() => sessions.LeaveAsync(Transport(server), connectionSessionId, operationId, cancellationToken));

    [McpServerTool(Name = "get_meeting_status", ReadOnly = true, UseStructuredContent = true)]
    [Description("Check meeting lifecycle even after disconnect or deletion, using the proof saved at join. Treat proof as private.")]
    public Task<McpResult<MeetingLifecycleStatusDto>> Status(Guid meetingId, string lifecycleProof, CancellationToken cancellationToken)
        => Safe(async () => await lifecycle.GetStatusAsync(new(meetingId, lifecycleProof), cancellationToken) ?? throw new McpOperationException("meeting_not_found"));

    [McpServerTool(Name = "prepare_attachment_transfer", UseStructuredContent = true)]
    [Description("Get a five-minute HTTP transfer ticket. Resolve relative URL against the MCP server origin. Send Bearer token in Authorization header, never URL. Upload: POST raw bytes with URL-encoded name and uploadRequestId query parameters. Persist uploadRequestId and reuse only for identical bytes/name. Download: GET with attachmentId supplied here. No arbitrary server paths or remote URL fetching. Tickets fail after disconnect/takeover/leave. At most 16 tickets per connection per five minutes.")]
    public Task<McpResult<McpTransferTicket>> Transfer(Guid connectionSessionId, string direction, McpServer server, CancellationToken cancellationToken, Guid? attachmentId = null)
        => Run(connectionSessionId, server, cancellationToken, (c, ct) =>
        {
            if (direction is not ("upload" or "download") || direction == "download" && (!attachmentId.HasValue || attachmentId == Guid.Empty)
                || direction == "upload" && attachmentId.HasValue) throw new McpOperationException("validation_failed");
            return Task.FromResult(tickets.Issue(Transport(server), c.ID, direction == "upload" ? "POST" : "GET", attachmentId));
        });
}
