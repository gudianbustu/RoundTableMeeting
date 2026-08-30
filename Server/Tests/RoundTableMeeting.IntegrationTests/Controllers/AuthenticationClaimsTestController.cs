using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RoundTableMeeting.Authentication;

namespace RoundTableMeeting.IntegrationTests.Controllers;

[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("__test/auth-claims")]
[Authorize]
public sealed class AuthenticationClaimsTestController : ControllerBase
{
    [HttpGet]
    public object Get() => new
    {
        claimTypes = User.Claims.Select(claim => claim.Type).ToArray(),
        hasCustomUserId = User.HasClaim(claim => claim.Type == AuthenticationConstants.ClaimType),
        hasNameIdentifier = User.HasClaim(claim => claim.Type == System.Security.Claims.ClaimTypes.NameIdentifier)
    };
}
