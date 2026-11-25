using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ModelContextProtocol;

namespace RoundTableMeeting.AgentCollaboration;

[ApiController, AllowAnonymous, ApiExplorerSettings(IgnoreApi = true)]
public sealed class PeerController(PeerTransport peers) : ControllerBase
{
    [HttpPost("roundtable-peer/v1/{operation}")]
    public async Task<IActionResult> Command(string operation, [FromBody] PeerCommand command, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var local = MultiMeetingTools.ForRequest(HttpContext);
        try
        {
            if (operation == "attend")
            {
                if (string.IsNullOrWhiteSpace(command.DisplayName) || string.IsNullOrWhiteSpace(command.InvitationKey))
                    return BadRequest(new PeerTransport.PeerError("invalid_invitation"));
                return new JsonResult(await local.Attend(command.MeetingCode, command.DisplayName, command.InvitationKey, ct), PeerJson.Options);
            }
            if (string.IsNullOrEmpty(command.Room)) return BadRequest(new PeerTransport.PeerError("invalid_room"));
            // Only a handle issued locally can be opened. This endpoint never forwards to a third site.
            var room = local.Local(command.Room);
            object value = operation switch
            {
                "poll" => await room.Poll(ct),
                "rename_self" when command.DisplayName is not null => await room.RenameSelf(command.DisplayName, ct),
                "list_participants" => await room.ListParticipants(ct),
                "history" => await room.History(ct, command.BeforeSequence, command.Limit),
                "reply" when command.Content is not null => await room.Reply(command.MessageId, command.Content, ct, command.AttachmentIds, command.ReplyToMessageId),
                "send_public_message" when command.Content is not null => await room.SendPublicMessage(command.ClientSubmissionId,
                    command.Content, ct, command.AttachmentIds),
                "send_private_message" when command.Content is not null => await room.SendPrivateMessage(command.ClientSubmissionId,
                    command.Content, ct, command.RecipientParticipantId, command.RecipientDisplayName, command.AttachmentIds),
                "skip" => await room.Skip(command.MessageId, ct),
                "transfer" => await room.Transfer(ct, command.AttachmentId),
                "leave" => await room.Leave(ct),
                _ => throw new McpException("invalid_peer_operation")
            };
            return new JsonResult(value, PeerJson.Options);
        }
        catch (Exception e) when (e is McpException or ArgumentException)
        { return BadRequest(new PeerTransport.PeerError(e is McpException ? e.Message : "invalid_invitation")); }
    }

    [HttpGet("roundtable-peer-files"), HttpPost("roundtable-peer-files"), DisableRequestSizeLimit]
    public async Task RelayFile([FromQuery] Guid? uploadRequestId, [FromQuery] string? name, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        var auth = Request.Headers.Authorization.ToString();
        var route = auth.StartsWith("Bearer ", StringComparison.Ordinal) ? peers.OpenTicket(auth[7..], Request.Method) : null;
        if (route is null) { Response.StatusCode = 401; return; }
        var site = PeerTransport.Normalize(route.Site);
        var path = "mcp-files";
        if (Request.Method == "POST")
        {
            if (uploadRequestId is null || string.IsNullOrEmpty(name)) { Response.StatusCode = 400; return; }
            if (Request.ContentLength > route.Limit) { Response.StatusCode = 413; return; }
            path += QueryString.Create(new Dictionary<string, string?> { ["uploadRequestId"] = uploadRequestId.ToString(), ["name"] = name });
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        using var request = new HttpRequestMessage(new HttpMethod(Request.Method), new Uri(site, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", route.Secret);
        if (Request.Method == "POST") request.Content = new UploadContent(Request.Body, route.Limit, deadline.Token);
        try
        {
            using var reply = await peers.ForSite(site).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if ((int)reply.StatusCode is >= 300 and < 400) { Response.StatusCode = 502; return; }
            var download = Request.Method == "GET" && reply.IsSuccessStatusCode;
            var limit = download ? route.Limit : 262144;
            if (reply.Content.Headers.ContentLength > limit) { Response.StatusCode = 502; return; }
            Response.StatusCode = (int)reply.StatusCode;
            Response.ContentType = download ? "application/octet-stream" : "application/json";
            if (download)
            {
                var disposition = new ContentDispositionHeaderValue("attachment");
                disposition.FileNameStar = reply.Content.Headers.ContentDisposition?.FileNameStar ?? "attachment";
                Response.Headers.ContentDisposition = disposition.ToString();
            }
            using var stream = await reply.Content.ReadAsStreamAsync(deadline.Token);
            await CopyLimited(stream, Response.Body, limit, deadline.Token);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or HttpRequestException or OperationCanceledException or McpException)
        {
            if (Response.HasStarted) HttpContext.Abort();
            else Response.StatusCode = e is HttpRequestException { InnerException: InvalidDataException } ? 413 : 502;
        }
    }

    private static async Task CopyLimited(Stream source, Stream target, long remaining, CancellationToken ct)
    {
        var buffer = new byte[65536];
        int count;
        while ((count = await source.ReadAsync(buffer, ct)) != 0)
        {
            if (count > remaining) throw new InvalidDataException("file_limit_exceeded");
            await target.WriteAsync(buffer.AsMemory(0, count), ct);
            remaining -= count;
        }
    }

    private sealed class UploadContent(Stream body, long limit, CancellationToken ct) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => CopyLimited(body, stream, limit, ct);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}

internal static class PeerJson
{
    // The wire format is independent of MVC's configured enum naming policy.
    internal static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web);
}
