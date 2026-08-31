using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace RoundTableMeeting.IntegrationTests.Controllers;

/// <summary>
/// 提供认证异常处理测试端点
/// </summary>
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[AllowAnonymous]
[Route("api/auth/test-failure")]
public sealed class AuthenticationFailureTestController : ControllerBase
{
    /// <summary>
    /// 返回测试端点所需的响应
    /// </summary>
    [HttpGet]
    public IActionResult Get() => throw new InvalidOperationException("authentication test failure");
}
