using Microsoft.AspNetCore.Mvc;
using RoundTableMeeting.Abstraction.Contracts;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Errors;
using Microsoft.AspNetCore.Authorization;

namespace RoundTableMeeting.Controllers;

/// <summary>
/// 提供系统运行状态查询端点
/// </summary>
[ApiController]
[Route("api/system")]
public sealed class SystemController(ISystemStatusService systemStatusService) : ControllerBase
{
    /// <summary>
    /// 返回服务、数据库和环境状态
    /// </summary>
    [AllowAnonymous]
    [HttpGet("status")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(SystemStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status500InternalServerError, "application/problem+json")]
    public async Task<ActionResult<SystemStatusDto>> GetStatusAsync(CancellationToken cancellationToken)
        => Ok(await systemStatusService.GetStatusAsync(cancellationToken));
}
