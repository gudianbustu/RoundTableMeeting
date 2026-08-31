using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RoundTableMeeting.Abstraction.Contracts.UserManagement;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Authentication;
using RoundTableMeeting.Errors;

namespace RoundTableMeeting.Controllers;

/// <summary>
/// 提供内部测试阶段的最小用户管理 API。
/// </summary>
[ApiController]
[Route("api/users")]
[Authorize]
[NoStoreResponse]
public sealed class UsersController(IUserManagementService userManagementService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<UserSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status500InternalServerError, "application/problem+json")]
    public async Task<ActionResult<IReadOnlyList<UserSummaryDto>>> List(CancellationToken cancellationToken)
        => Ok(await userManagementService.ListUsersAsync(cancellationToken));

    [HttpPost]
    [RequireBrowserOrigin(true)]
    [ProducesResponseType(typeof(UserSummaryDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status415UnsupportedMediaType, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status500InternalServerError, "application/problem+json")]
    public async Task<IActionResult> Create(CreateUserRequest request, CancellationToken cancellationToken)
    {
        var result = await userManagementService.CreateUserAsync(request, cancellationToken);
        return result.Status switch
        {
            UserManagementStatus.Success => StatusCode(StatusCodes.Status201Created, result.User),
            UserManagementStatus.UsernameConflict => ApiProblemDetailsWriter.CreateResult(HttpContext, StatusCodes.Status409Conflict, "username_conflict"),
            UserManagementStatus.ValidationFailed => ApiProblemDetailsWriter.CreateResult(HttpContext, StatusCodes.Status400BadRequest, "validation_failed", result.Errors),
            _ => ApiProblemDetailsWriter.CreateResult(HttpContext, StatusCodes.Status500InternalServerError, "internal_error")
        };
    }

    [HttpPatch("{userId:guid}/nickname")]
    [RequireBrowserOrigin(true)]
    [ProducesResponseType(typeof(UserSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status415UnsupportedMediaType, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status500InternalServerError, "application/problem+json")]
    public async Task<IActionResult> UpdateNickname(Guid userId, UpdateNicknameRequest request, CancellationToken cancellationToken)
    {
        var result = await userManagementService.UpdateNicknameAsync(userId, request, cancellationToken);
        return result.Status switch
        {
            UserManagementStatus.Success => Ok(result.User),
            UserManagementStatus.UserNotFound => ApiProblemDetailsWriter.CreateResult(HttpContext, StatusCodes.Status404NotFound, "user_not_found"),
            UserManagementStatus.ValidationFailed => ApiProblemDetailsWriter.CreateResult(HttpContext, StatusCodes.Status400BadRequest, "validation_failed", result.Errors),
            _ => ApiProblemDetailsWriter.CreateResult(HttpContext, StatusCodes.Status500InternalServerError, "internal_error")
        };
    }

    [HttpPost("{userId:guid}/reset-password")]
    [RequireBrowserOrigin]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status500InternalServerError, "application/problem+json")]
    public async Task<IActionResult> ResetPassword(Guid userId, CancellationToken cancellationToken)
    {
        var result = await userManagementService.ResetPasswordAsync(userId, cancellationToken);
        return result.Status switch
        {
            UserManagementStatus.Success => NoContent(),
            UserManagementStatus.UserNotFound => ApiProblemDetailsWriter.CreateResult(HttpContext, StatusCodes.Status404NotFound, "user_not_found"),
            _ => ApiProblemDetailsWriter.CreateResult(HttpContext, StatusCodes.Status500InternalServerError, "internal_error")
        };
    }
}
