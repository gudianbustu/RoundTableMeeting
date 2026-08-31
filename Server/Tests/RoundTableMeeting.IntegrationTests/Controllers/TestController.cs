using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using System.ComponentModel.DataAnnotations;

namespace RoundTableMeeting.IntegrationTests.Controllers;

/// <summary>
/// 提供应用部件、异常和校验测试端点
/// </summary>
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("__test")]
[AllowAnonymous]
public sealed class TestController : ControllerBase
{
    [HttpGet("application-part")]
    public IActionResult ApplicationPart() => Ok(new { loaded = true });

    [HttpGet("throw")]
    public IActionResult Throw() => throw new InvalidOperationException("test exception");

    [HttpPost("validation")]
    public IActionResult Validation(ValidationRequest request) => Ok(request);
}

/// <summary>
/// 表示模型校验测试使用的请求体
/// </summary>
public sealed class ValidationRequest
{
    /// <summary>
    /// 获取模型校验测试使用的必填值
    /// </summary>
    [Required]
    public string? Name { get; init; }
}
