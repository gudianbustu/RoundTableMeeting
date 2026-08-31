using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RoundTableMeeting.Authentication;

namespace RoundTableMeeting.IntegrationTests.Controllers;

/// <summary>
/// 提供认证声明契约测试端点
/// </summary>
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("__test/auth-claims")]
[Authorize]
public sealed class AuthenticationClaimsTestController : ControllerBase
{
    /// <summary>
    /// 返回测试端点所需的响应
    /// </summary>
    [HttpGet]
    public object Get() => new
    {
        claimTypes = User.Claims.Select(claim => claim.Type).ToArray(),
        hasCustomUserId = User.HasClaim(claim => claim.Type == AuthenticationConstants.ClaimType),
        hasNameIdentifier = User.HasClaim(claim => claim.Type == System.Security.Claims.ClaimTypes.NameIdentifier)
    };
}
