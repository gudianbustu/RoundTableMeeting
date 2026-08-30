using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace RoundTableMeeting.IntegrationTests.Controllers;

public sealed class AlwaysForbiddenRequirement : IAuthorizationRequirement;

public sealed class AlwaysForbiddenHandler : AuthorizationHandler<AlwaysForbiddenRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AlwaysForbiddenRequirement requirement) => Task.CompletedTask;
}

[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("__test/forbidden")]
[Authorize(Policy = "AlwaysForbidden")]
public sealed class ForbiddenTestController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { reached = true });
}
