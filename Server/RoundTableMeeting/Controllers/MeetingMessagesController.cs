using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Authentication;
using RoundTableMeeting.Errors;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.RealtimeChat;

namespace RoundTableMeeting.Controllers;

[ApiController]
[Authorize]
[Route("api/meetings/{meetingId:guid}/messages")]
[NoStoreResponse]
public sealed class MeetingMessagesController(IMessageHistoryService history, IMessageAppendService append) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(MessagePageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status410Gone, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status500InternalServerError, "application/problem+json")]
    public async Task<IActionResult> Get(Guid meetingId, [FromQuery] MessagePageRequest request, CancellationToken cancellationToken)
    {
        var result = await history.GetPageAsync(meetingId, request, cancellationToken);
        return result.Status switch
        {
            MessageOperationStatus.Success => Ok(result.Data),
            MessageOperationStatus.Unauthenticated => ApiProblemDetailsWriter.CreateResult(HttpContext, 401, "unauthenticated"),
            MessageOperationStatus.ValidationFailed => ApiProblemDetailsWriter.CreateResult(HttpContext, 400, "validation_failed", result.Errors),
            MessageOperationStatus.MeetingDeleting => ApiProblemDetailsWriter.CreateResult(HttpContext, 410, "meeting_deleting"),
            MessageOperationStatus.MeetingDeleted => ApiProblemDetailsWriter.CreateResult(HttpContext, 410, "meeting_deleted"),
            _ => ApiProblemDetailsWriter.CreateResult(HttpContext, 404, "meeting_not_found")
        };
    }

    [HttpPost]
    [RequireBrowserOrigin(true)]
    [ServiceFilter(typeof(MessageSendRateLimitFilter))]
    [ProducesResponseType(typeof(SubmitMessageResultDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(SubmitMessageResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status410Gone, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status415UnsupportedMediaType, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status429TooManyRequests, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status500InternalServerError, "application/problem+json")]
    public async Task<IActionResult> Post(Guid meetingId, SubmitMessageRequest request, CancellationToken cancellationToken)
    {
        var result = await append.AppendHumanAsync(meetingId, request, cancellationToken);
        return result.Status switch
        {
            MessageOperationStatus.Created => StatusCode(StatusCodes.Status201Created, new SubmitMessageResultDto(result.Data!, false)),
            MessageOperationStatus.Replayed => Ok(new SubmitMessageResultDto(result.Data!, true)),
            MessageOperationStatus.Unauthenticated => ApiProblemDetailsWriter.CreateResult(HttpContext, 401, "unauthenticated"),
            MessageOperationStatus.ValidationFailed => ApiProblemDetailsWriter.CreateResult(HttpContext, 400, "validation_failed", result.Errors),
            MessageOperationStatus.MessageNotFound => ApiProblemDetailsWriter.CreateResult(HttpContext, 404, "message_not_found"),
            MessageOperationStatus.AttachmentUnavailable => ApiProblemDetailsWriter.CreateResult(HttpContext, 404, "attachment_unavailable"),
            MessageOperationStatus.RecipientUnavailable => ApiProblemDetailsWriter.CreateResult(HttpContext, 409, "recipient_unavailable"),
            MessageOperationStatus.ReplyScopeMismatch => ApiProblemDetailsWriter.CreateResult(HttpContext, 409, "reply_scope_mismatch"),
            MessageOperationStatus.SubmissionMismatch => ApiProblemDetailsWriter.CreateResult(HttpContext, 409, "message_submission_mismatch"),
            MessageOperationStatus.MembershipLeft => ApiProblemDetailsWriter.CreateResult(HttpContext, 409, "membership_left"),
            MessageOperationStatus.Conflict => ApiProblemDetailsWriter.CreateResult(HttpContext, 409, "message_append_conflict"),
            MessageOperationStatus.MeetingDeleting => ApiProblemDetailsWriter.CreateResult(HttpContext, 410, "meeting_deleting"),
            MessageOperationStatus.MeetingDeleted => ApiProblemDetailsWriter.CreateResult(HttpContext, 410, "meeting_deleted"),
            _ => ApiProblemDetailsWriter.CreateResult(HttpContext, 404, "meeting_not_found")
        };
    }
}
