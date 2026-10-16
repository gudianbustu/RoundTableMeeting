using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RoundTableMeeting.Abstraction.Services;
namespace RoundTableMeeting.AgentCollaboration;

[ApiController, AllowAnonymous, Route("mcp-files")]
public sealed class McpAttachmentTransferController(McpTransferTickets tickets, CurrentMcpSessionService sessions, IAttachmentService attachments) : ControllerBase
{
    [HttpPost, DisableRequestSizeLimit]
    public Task Upload([FromQuery] Guid uploadRequestId, [FromQuery] string name, CancellationToken ct)
        => Transfer("POST", async (binding, meetingId, token) =>
        {
            var result = await attachments.UploadAsync(meetingId, uploadRequestId, name, Request.Body, token);
            await Response.WriteAsJsonAsync(result, token);
        }, ct);

    [HttpGet]
    public Task Download(CancellationToken ct) => Transfer("GET", async (binding, meetingId, token) =>
    {
        var file = await attachments.OpenAsync(meetingId, binding.AttachmentId!.Value, token);
        await using var content = file.Content;
        await new FileStreamResult(content, file.MediaType) { FileDownloadName = file.Name, EnableRangeProcessing = true }.ExecuteResultAsync(ControllerContext);
    }, ct);

    private async Task Transfer(string method, Func<McpTransferBinding, Guid, CancellationToken, Task> action, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        var authorization = Request.Headers.Authorization.ToString();
        var binding = authorization.StartsWith("Bearer ", StringComparison.Ordinal) ? tickets.Find(authorization[7..]) : null;
        if (binding is null || binding.Method != method) { Response.StatusCode = 401; return; }
        try
        {
            await sessions.ExecuteAsync(binding.Transport, binding.ConnectionId, async (c, token) =>
            { var original = HttpContext.RequestAborted; try { HttpContext.RequestAborted = token; await action(binding, c.MeetingId, token); return true; } finally { HttpContext.RequestAborted = original; } }, ct);
        }
        catch (McpOperationException) when (!Response.HasStarted) { Response.StatusCode = 401; }
        catch (AttachmentOperationException e) when (!Response.HasStarted)
        { Response.StatusCode = e.Status; await Response.WriteAsJsonAsync(new { code = e.Code }, ct); }
    }
}
