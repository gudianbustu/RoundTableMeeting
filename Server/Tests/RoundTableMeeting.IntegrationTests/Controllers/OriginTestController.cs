using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RoundTableMeeting.Authentication;

namespace RoundTableMeeting.IntegrationTests.Controllers;

/// <summary>
/// 提供浏览器来源校验测试端点
/// </summary>
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("__test/origin")]
public sealed class OriginTestController : ControllerBase
{
    [HttpPost("json")]
    [AllowAnonymous]
    [RequireBrowserOrigin(requireJsonBody: true)]
    public IActionResult PostJson(OriginJsonRequest request) => Ok(new { method = "POST", request.Name });

    [HttpPatch("json")]
    [AllowAnonymous]
    [RequireBrowserOrigin(requireJsonBody: true)]
    public IActionResult PatchJson(OriginJsonRequest request) => Ok(new { method = "PATCH", request.Name });

    [HttpPut("json")]
    [AllowAnonymous]
    [RequireBrowserOrigin(requireJsonBody: true)]
    public IActionResult PutJson(OriginJsonRequest request) => Ok(new { method = "PUT", request.Name });

    [HttpPost("no-json")]
    [AllowAnonymous]
    [RequireBrowserOrigin]
    public IActionResult PostWithoutJsonRequirement() => Ok(new { method = "POST" });

    [HttpDelete("no-json")]
    [AllowAnonymous]
    [RequireBrowserOrigin]
    public IActionResult DeleteWithoutJsonRequirement() => Ok(new { method = "DELETE" });

    [HttpPost("unmarked")]
    [AllowAnonymous]
    public IActionResult Unmarked() => Ok(new { method = "POST" });

    [HttpPost("authorized")]
    [Authorize]
    [RequireBrowserOrigin]
    public IActionResult Authorized() => Ok(new { reached = true });
}

/// <summary>
/// 表示来源保护端点使用的 JSON 请求体
/// </summary>
public sealed class OriginJsonRequest
{
    /// <summary>
    /// 获取来源保护端点使用的必填测试值
    /// </summary>
    [Required]
    public string? Name { get; init; }
}
