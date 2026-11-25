using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RoundTableMeeting.Abstraction.Enums;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Authentication;
using RoundTableMeeting.Errors;
using RoundTableMeeting.Meetings;

namespace RoundTableMeeting.Controllers;

public sealed record RenameMeetingParticipantRequest(string DisplayName);

[ApiController, Authorize, NoStoreResponse]
public sealed class MeetingNicknameController(ICurrentUserContext currentUser, IMeetingRealtimeGate gate, MeetingNicknameService names) : ControllerBase
{
    [RequireBrowserOrigin(true)]
    [HttpPut("api/meetings/{meetingId:guid}/participants/me/display-name")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Rename(Guid meetingId, RenameMeetingParticipantRequest request, CancellationToken ct)
    {
        if (currentUser.UserId is not Guid id) return Unauthorized();
        using var lease = await gate.AcquireSharedAsync(meetingId, ct);
        if (lease is null) return ApiProblemDetailsWriter.CreateResult(HttpContext, 404, "meeting_not_found");
        return await names.RenameHumanAsync(meetingId, id, request.DisplayName, ct) switch
        {
            MeetingOperationStatus.Success => NoContent(),
            MeetingOperationStatus.ValidationFailed => ApiProblemDetailsWriter.CreateResult(HttpContext, 400, "validation_failed"),
            MeetingOperationStatus.DisplayNameTaken => ApiProblemDetailsWriter.CreateResult(HttpContext, 409, "display_name_taken"),
            _ => ApiProblemDetailsWriter.CreateResult(HttpContext, 404, "meeting_not_found")
        };
    }
}
