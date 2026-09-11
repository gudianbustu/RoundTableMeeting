using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RoundTableMeeting.Abstraction.Contracts.Authentication;
using RoundTableMeeting.Abstraction.Contracts.UserManagement;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Authentication;
using RoundTableMeeting.Errors;

namespace RoundTableMeeting.Controllers;

[ApiController, Authorize, Route("api/account"), NoStoreResponse]
public sealed class AccountController(IAccountSettingsService settings) : ControllerBase
{
    [HttpPatch("nickname"), RequireBrowserOrigin(true)]
    [ProducesResponseType(typeof(CurrentUserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status415UnsupportedMediaType, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status500InternalServerError, "application/problem+json")]
    public async Task<IActionResult> UpdateNickname(UpdateNicknameRequest request, CancellationToken cancellationToken)
        => ToResult(await settings.UpdateMyNicknameAsync(request, cancellationToken));

    [HttpPost("change-password"), RequireBrowserOrigin(true)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status415UnsupportedMediaType, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status500InternalServerError, "application/problem+json")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken cancellationToken)
        => ToResult(await settings.ChangeMyPasswordAsync(request, cancellationToken));

    private IActionResult ToResult(AccountSettingsResult result) => result.Status switch
    {
        AccountSettingsStatus.Success => result.User is null ? NoContent() : Ok(result.User),
        AccountSettingsStatus.Unauthenticated => ApiProblemDetailsWriter.CreateResult(HttpContext, 401, "unauthenticated"),
        AccountSettingsStatus.ValidationFailed => ApiProblemDetailsWriter.CreateResult(HttpContext, 400, "validation_failed", result.Errors),
        _ => ApiProblemDetailsWriter.CreateResult(HttpContext, 500, "internal_error")
    };
}
