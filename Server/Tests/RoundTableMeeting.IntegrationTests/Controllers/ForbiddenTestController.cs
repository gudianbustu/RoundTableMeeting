using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace RoundTableMeeting.IntegrationTests.Controllers;

/// <summary>
/// 表示永远不会满足的授权要求
/// </summary>
public sealed class AlwaysForbiddenRequirement : IAuthorizationRequirement;

/// <summary>
/// 保持授权要求未满足以测试拒绝访问
/// </summary>
public sealed class AlwaysForbiddenHandler : AuthorizationHandler<AlwaysForbiddenRequirement>
{
    /// <summary>
    /// 保持授权要求未满足
    /// </summary>
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AlwaysForbiddenRequirement requirement) => Task.CompletedTask;
}

/// <summary>
/// 提供拒绝访问响应测试端点
/// </summary>
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("__test/forbidden")]
[Authorize(Policy = "AlwaysForbidden")]
public sealed class ForbiddenTestController : ControllerBase
{
    /// <summary>
    /// 返回测试端点所需的响应
    /// </summary>
    [HttpGet]
    public IActionResult Get() => Ok(new { reached = true });
}
