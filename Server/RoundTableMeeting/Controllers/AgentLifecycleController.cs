using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RoundTableMeeting.Abstraction.Contracts.Meetings;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Authentication;
using RoundTableMeeting.Errors;
namespace RoundTableMeeting.Controllers;

[ApiController]
[Route("api/agent/v1/lifecycle-status")]
[AllowAnonymous]
[NoStoreResponse]
public sealed class AgentLifecycleController(IMeetingLifecycleService service) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(typeof(MeetingLifecycleStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Status(MeetingLifecycleStatusRequest request, CancellationToken cancellationToken)
    {
        var result = await service.GetStatusAsync(request, cancellationToken);
        return result is null ? ApiProblemDetailsWriter.CreateResult(HttpContext, 404, "meeting_not_found") : Ok(result);
    }
}
