using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Errors;
using RoundTableMeeting.Authentication;
namespace RoundTableMeeting.Controllers;

[ApiController, Authorize, NoStoreResponse]
[ProducesResponseType(typeof(ApiProblemDetails), 400)]
[ProducesResponseType(typeof(ApiProblemDetails), 401)]
[ProducesResponseType(typeof(ApiProblemDetails), 404)]
[ProducesResponseType(typeof(ApiProblemDetails), 409)]
[ProducesResponseType(typeof(ApiProblemDetails), 410)]
[ProducesResponseType(typeof(ApiProblemDetails), 413)]
[ProducesResponseType(typeof(ApiProblemDetails), 503)]
[Route("api/meetings/{meetingId:guid}")]
public sealed class MessageLookupController(IMessageLookupService lookup) : ControllerBase
{
    [HttpGet("messages/{messageId:guid}")]
    [ProducesResponseType(typeof(MessageDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMessage(Guid meetingId, Guid messageId, CancellationToken ct)
        => Result(await lookup.GetMessageAsync(meetingId, messageId, ct));
    [HttpGet("submissions/{submissionId:guid}")]
    [ProducesResponseType(typeof(SubmissionStatusDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSubmission(Guid meetingId, Guid submissionId, CancellationToken ct)
        => Result(await lookup.GetSubmissionAsync(meetingId, submissionId, ct));
    private IActionResult Result<T>(MessageOperationResult<T> result) => result.Status switch
    {
        MessageOperationStatus.Success => Ok(result.Data),
        MessageOperationStatus.Unauthenticated => ApiProblemDetailsWriter.CreateResult(HttpContext, 401, "unauthenticated"),
        MessageOperationStatus.MeetingDeleting => ApiProblemDetailsWriter.CreateResult(HttpContext, 410, "meeting_deleting"),
        MessageOperationStatus.MeetingDeleted => ApiProblemDetailsWriter.CreateResult(HttpContext, 410, "meeting_deleted"),
        MessageOperationStatus.MessageNotFound => ApiProblemDetailsWriter.CreateResult(HttpContext, 404, "message_not_found"),
        _ => ApiProblemDetailsWriter.CreateResult(HttpContext, 404, "meeting_not_found")
    };
}
