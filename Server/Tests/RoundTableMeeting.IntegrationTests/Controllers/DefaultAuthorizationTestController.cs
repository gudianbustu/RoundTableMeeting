using Microsoft.AspNetCore.Mvc;

namespace RoundTableMeeting.IntegrationTests.Controllers;

/// <summary>
/// 提供默认授权策略测试端点
/// </summary>
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("__test/default-authorization")]
public sealed class DefaultAuthorizationTestController : ControllerBase
{
    /// <summary>
    /// 返回测试端点所需的响应
    /// </summary>
    [HttpGet]
    public IActionResult Get() => Ok(new { reached = true });
}
