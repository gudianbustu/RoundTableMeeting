using Microsoft.AspNetCore.Mvc;

namespace RoundTableMeeting.IntegrationTests.Controllers;

[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("__test/default-authorization")]
public sealed class DefaultAuthorizationTestController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { reached = true });
}
