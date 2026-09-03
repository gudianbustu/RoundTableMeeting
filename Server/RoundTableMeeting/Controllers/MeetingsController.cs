using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RoundTableMeeting.Abstraction.Contracts.Meetings;
using RoundTableMeeting.Abstraction.Enums;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Authentication;
using RoundTableMeeting.Errors;
using RoundTableMeeting.Meetings;

namespace RoundTableMeeting.Controllers;

[ApiController]
[Route("api/meetings")]
[Authorize]
[NoStoreResponse]
public sealed class MeetingsController(IMeetingWorkspaceService service) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<MeetingListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status500InternalServerError, "application/problem+json")]
    public async Task<IActionResult> List(CancellationToken cancellationToken) => Ok(await service.ListAsync(cancellationToken));

    [HttpPost]
    [RequireBrowserOrigin(true)]
    [ProducesResponseType(typeof(CreateMeetingResultDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(CreateMeetingResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status415UnsupportedMediaType, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status500InternalServerError, "application/problem+json")]
    public async Task<IActionResult> Create(CreateMeetingRequest request, CancellationToken cancellationToken)
    {
        var result = await service.CreateAsync(request, cancellationToken);
        return result.Status switch
        {
            MeetingOperationStatus.Created => CreatedAtAction(nameof(Get), new { meetingId = result.Data!.Meeting.MeetingId }, result.Data),
            MeetingOperationStatus.Replayed => Ok(result.Data),
            MeetingOperationStatus.ValidationFailed => ApiProblemDetailsWriter.CreateResult(HttpContext, 400, "validation_failed", result.Errors),
            MeetingOperationStatus.CreationRequestMismatch => ApiProblemDetailsWriter.CreateResult(HttpContext, 409, "creation_request_mismatch"),
            MeetingOperationStatus.CreationRequestConsumed => ApiProblemDetailsWriter.CreateResult(HttpContext, 409, "creation_request_consumed"),
            _ => ApiProblemDetailsWriter.CreateResult(HttpContext, 500, "internal_error")
        };
    }

    [HttpGet("{meetingId:guid}")]
    [ProducesResponseType(typeof(MeetingDetailsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status500InternalServerError, "application/problem+json")]
    public async Task<IActionResult> Get(Guid meetingId, CancellationToken cancellationToken)
    {
        var result = await service.GetAsync(meetingId, cancellationToken);
        return result.Status switch
        {
            MeetingOperationStatus.Success => Ok(result.Data),
            MeetingOperationStatus.Unauthenticated => ApiProblemDetailsWriter.CreateResult(HttpContext, 401, "unauthenticated"),
            _ => ApiProblemDetailsWriter.CreateResult(HttpContext, 404, "meeting_not_found")
        };
    }

    [HttpPost("{meetingId:guid}/invitation")]
    [RequireBrowserOrigin(false)]
    [ProducesResponseType(typeof(MeetingInvitationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status500InternalServerError, "application/problem+json")]
    public async Task<IActionResult> Invitation(Guid meetingId, CancellationToken cancellationToken)
    {
        var result = await service.InvitationAsync(meetingId, cancellationToken);
        return result.Status == MeetingOperationStatus.Success ? Ok(result.Data) : ApiProblemDetailsWriter.CreateResult(HttpContext, 404, "meeting_not_found");
    }

    [HttpPost("join/preview")]
    [RequireBrowserOrigin(true)]
    [ServiceFilter(typeof(MeetingJoinRateLimitFilter))]
    [ProducesResponseType(typeof(JoinPreviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status415UnsupportedMediaType, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status429TooManyRequests, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status500InternalServerError, "application/problem+json")]
    public async Task<IActionResult> Preview(PreviewJoinRequest request, CancellationToken cancellationToken)
    {
        var result = await service.PreviewJoinAsync(request, cancellationToken);
        return result.Status switch
        {
            MeetingOperationStatus.Success => Ok(result.Data),
            MeetingOperationStatus.ValidationFailed => ApiProblemDetailsWriter.CreateResult(HttpContext, 400, "validation_failed", result.Errors),
            _ => ApiProblemDetailsWriter.CreateResult(HttpContext, 404, "meeting_not_found")
        };
    }

    [HttpPost("join")]
    [RequireBrowserOrigin(true)]
    [ServiceFilter(typeof(MeetingJoinRateLimitFilter))]
    [ProducesResponseType(typeof(MeetingDetailsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status415UnsupportedMediaType, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status429TooManyRequests, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status500InternalServerError, "application/problem+json")]
    public async Task<IActionResult> Join(JoinMeetingRequest request, CancellationToken cancellationToken)
    {
        var result = await service.JoinAsync(request, cancellationToken);
        return result.Status switch
        {
            MeetingOperationStatus.Success => Ok(result.Data),
            MeetingOperationStatus.AlreadyMember => Ok(result.Data),
            MeetingOperationStatus.DisplayNameTaken => ApiProblemDetailsWriter.CreateResult(HttpContext, 409, "display_name_taken"),
            MeetingOperationStatus.JoinConflict => ApiProblemDetailsWriter.CreateResult(HttpContext, 409, "meeting_join_conflict"),
            MeetingOperationStatus.ValidationFailed => ApiProblemDetailsWriter.CreateResult(HttpContext, 400, "validation_failed", result.Errors),
            _ => ApiProblemDetailsWriter.CreateResult(HttpContext, 404, "meeting_not_found")
        };
    }

    [HttpPost("{meetingId:guid}/deletion")]
    [RequireBrowserOrigin(true)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status415UnsupportedMediaType, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status500InternalServerError, "application/problem+json")]
    public async Task<IActionResult> Delete(Guid meetingId, DeleteMeetingRequest request, CancellationToken cancellationToken)
    {
        var result = await service.DeleteAsync(meetingId, request.ConfirmationName, cancellationToken);
        return result.Status switch
        {
            MeetingOperationStatus.Accepted => Accepted(result.Data),
            MeetingOperationStatus.DeleteBusy => ApiProblemDetailsWriter.CreateResult(HttpContext, 409, "meeting_delete_busy"),
            MeetingOperationStatus.CreatorRequired => ApiProblemDetailsWriter.CreateResult(HttpContext, 403, "meeting_creator_required"),
            MeetingOperationStatus.ValidationFailed => ApiProblemDetailsWriter.CreateResult(HttpContext, 400, "validation_failed", result.Errors),
            _ => ApiProblemDetailsWriter.CreateResult(HttpContext, 404, "meeting_not_found")
        };
    }
}
