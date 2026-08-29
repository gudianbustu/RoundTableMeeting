using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

namespace RoundTableMeeting.IntegrationTests.Controllers;

[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("__test")]
public sealed class TestController : ControllerBase
{
    [HttpGet("application-part")]
    public IActionResult ApplicationPart() => Ok(new { loaded = true });

    [HttpGet("throw")]
    public IActionResult Throw() => throw new InvalidOperationException("test exception");

    [HttpPost("validation")]
    public IActionResult Validation(ValidationRequest request) => Ok(request);
}

public sealed class ValidationRequest
{
    [Required]
    public string? Name { get; init; }
}
