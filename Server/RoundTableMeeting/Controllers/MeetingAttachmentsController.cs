using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Application.RealtimeChat;
using RoundTableMeeting.Authentication;
using RoundTableMeeting.Errors;
using RoundTableMeeting.RealtimeChat;
namespace RoundTableMeeting.Controllers;

[ApiController, Authorize, NoStoreResponse]
[ProducesResponseType(typeof(ApiProblemDetails), 400)]
[ProducesResponseType(typeof(ApiProblemDetails), 401)]
[ProducesResponseType(typeof(ApiProblemDetails), 404)]
[ProducesResponseType(typeof(ApiProblemDetails), 409)]
[ProducesResponseType(typeof(ApiProblemDetails), 410)]
[ProducesResponseType(typeof(ApiProblemDetails), 413)]
[ProducesResponseType(typeof(ApiProblemDetails), 503)]
[Route("api/meetings/{meetingId:guid}/attachments")]
public sealed class MeetingAttachmentsController(IAttachmentService attachments, IOptions<AttachmentOptions> options) : ControllerBase
{
    [HttpPost, RequireBrowserOrigin(false), DisableRequestSizeLimit, StreamingUpload]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(AttachmentDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Upload(Guid meetingId, [FromQuery] Guid? uploadRequestId, CancellationToken ct)
    {
        try
        {
            if (!MediaTypeHeaderValue.TryParse(Request.ContentType, out var type) || type.MediaType != "multipart/form-data")
                return Error("validation_failed", 400);
            var boundary = HeaderUtilities.RemoveQuotes(type.Boundary).Value;
            if (string.IsNullOrEmpty(boundary) || boundary.Length > 128) return Error("validation_failed", 400);
            var reader = new MultipartReader(boundary, Request.Body) { HeadersCountLimit = 16, HeadersLengthLimit = 8192 };
            var requestId = uploadRequestId;
            var section = await reader.ReadNextSectionAsync(ct);
            if (section is null) return Error("validation_failed", 400);
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)) return Error("validation_failed", 400);
            if (!disposition.FileName.HasValue && !disposition.FileNameStar.HasValue)
            {
                if (HeaderUtilities.RemoveQuotes(disposition.Name) != "uploadRequestId") return Error("validation_failed", 400);
                var bytes = new byte[128]; var count = 0;
                while (count < bytes.Length)
                {
                    var read = await section.Body.ReadAsync(bytes.AsMemory(count), ct); if (read == 0) break; count += read;
                }
                if (count == bytes.Length || !Guid.TryParse(Encoding.UTF8.GetString(bytes, 0, count), out var parsed)
                    || requestId.HasValue && requestId != parsed) return Error("validation_failed", 400);
                requestId = parsed; section = await reader.ReadNextSectionAsync(ct);
                if (section is null || !ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out disposition)) return Error("validation_failed", 400);
            }
            if (requestId is not Guid id || id == Guid.Empty || disposition.DispositionType != "form-data"
                || HeaderUtilities.RemoveQuotes(disposition.Name) != "file" || !disposition.FileName.HasValue && !disposition.FileNameStar.HasValue)
                return Error("validation_failed", 400);
            var name = HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value ?? "";
            var result = await attachments.UploadAsync(meetingId, id, name, section.Body, ct);
            if (await reader.ReadNextSectionAsync(ct) is not null) return Error("validation_failed", 400);
            return Ok(result);
        }
        catch (AttachmentOperationException ex) { return Error(ex.Code, ex.Status, ex.LimitBytes); }
        catch (InvalidDataException) { return Error("validation_failed", 400); }
        catch (IOException) { return Error("invalid_multipart", 400); }
    }
    [HttpGet]
    [ProducesResponseType(typeof(AttachmentPageDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(Guid meetingId, [FromQuery] MessagePageRequest request, CancellationToken ct)
    {
        try { return Ok(await attachments.ListAsync(meetingId, request, ct)); }
        catch (AttachmentOperationException ex) { return Error(ex.Code, ex.Status, ex.LimitBytes); }
    }
    [HttpGet("{attachmentId:guid}/content")]
    public async Task<IActionResult> Content(Guid meetingId, Guid attachmentId, [FromQuery] string mode = "download", CancellationToken ct = default)
    {
        if (mode is not ("download" or "preview")) return Error("validation_failed", 400);
        try
        {
            var file = await attachments.OpenAsync(meetingId, attachmentId, ct);
            var textPreview = mode == "preview" && file.MediaType == "application/octet-stream"
                && Path.GetExtension(file.Name).ToLowerInvariant() is ".txt" or ".md" or ".log" or ".csv" or ".tsv" or ".json" or ".xml" or ".yaml" or ".yml" or ".ini" or ".conf";
            var mediaPreviewType = mode == "preview" && file.MediaType == "application/octet-stream"
                ? Path.GetExtension(file.Name).ToLowerInvariant() switch
                {
                    ".mp3" => "audio/mpeg", ".wav" => "audio/wav", ".ogg" or ".oga" => "audio/ogg",
                    ".m4a" => "audio/mp4", ".aac" => "audio/aac", ".flac" => "audio/flac",
                    ".mp4" or ".m4v" => "video/mp4", ".webm" => "video/webm", ".ogv" => "video/ogg",
                    ".mov" => "video/quicktime", _ => null
                } : null;
            var inline = textPreview || mediaPreviewType is not null || mode == "preview" && file.MediaType is "image/png" or "image/jpeg" or "image/gif" or "image/webp" or "application/pdf";
            Response.Headers.XContentTypeOptions = "nosniff";
            Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'; frame-ancestors 'self'";
            Response.Headers.CacheControl = "no-store";
            var disposition = new ContentDispositionHeaderValue(inline ? "inline" : "attachment");
            disposition.SetHttpFileName(file.Name); Response.Headers.ContentDisposition = disposition.ToString();
            return File(file.Content, textPreview ? "text/plain" : mediaPreviewType ?? file.MediaType, enableRangeProcessing: true);
        }
        catch (AttachmentOperationException ex) { return Error(ex.Code, ex.Status, ex.LimitBytes); }
    }
    [HttpGet("/api/system/capabilities")]
    [ProducesResponseType(typeof(CommunicationCapabilitiesDto), StatusCodes.Status200OK)]
    public IActionResult Capabilities() => Ok(new CommunicationCapabilitiesDto("participant-sequence-v2", options.Value.MaxFileBytes, "bytes", true));
    private IActionResult Error(string code, int status, long? limit = null)
    {
        var problem = RoundTableMeeting.Errors.ProblemDetailsFactory.Create(HttpContext, status, code);
        if (limit.HasValue) problem.Extensions["limitBytes"] = limit.Value;
        return new ObjectResult(problem) { StatusCode = status, ContentTypes = { "application/problem+json" } };
    }
}
