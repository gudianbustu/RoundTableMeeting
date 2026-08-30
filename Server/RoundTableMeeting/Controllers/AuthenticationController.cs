using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using RoundTableMeeting.Abstraction.Contracts.Authentication;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Authentication;
using RoundTableMeeting.Errors;
using AppAuthenticationService = RoundTableMeeting.Abstraction.Services.IAuthenticationService;

namespace RoundTableMeeting.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthenticationController(AppAuthenticationService authenticationService) : ControllerBase
{
    [HttpPost("login")]
    [AllowAnonymous]
    [RequireBrowserOrigin(true)]
    [ProducesResponseType(typeof(CurrentUserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status415UnsupportedMediaType, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status500InternalServerError, "application/problem+json")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await authenticationService.AuthenticateAsync(request, cancellationToken);
        if (result.Status == AuthenticateStatus.ValidationFailed) return AuthenticationProblemDetailsExtensions.Result(HttpContext, StatusCodes.Status400BadRequest, "validation_failed", result.Errors);
        if (result.Status == AuthenticateStatus.InvalidCredentials) return AuthenticationProblemDetailsExtensions.Result(HttpContext, StatusCodes.Status401Unauthorized, "invalid_credentials");
        var user = result.User!;
        await HttpContext.SignInAsync(AuthenticationConstants.Scheme, new ClaimsPrincipal(new ClaimsIdentity([new Claim(AuthenticationConstants.ClaimType, user.UserId.ToString())], AuthenticationConstants.Scheme)), new AuthenticationProperties { IsPersistent = false, AllowRefresh = false });
        return Ok(user);
    }

    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType(typeof(CurrentUserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status500InternalServerError, "application/problem+json")]
    public async Task<IActionResult> Me(CancellationToken cancellationToken)
    {
        var result = await authenticationService.GetCurrentUserAsync(cancellationToken);
        if (result.Status == CurrentUserStatus.Unauthenticated)
        {
            await HttpContext.SignOutAsync(AuthenticationConstants.Scheme);
            return AuthenticationProblemDetailsExtensions.Result(HttpContext, StatusCodes.Status401Unauthorized, "unauthenticated");
        }
        return Ok(result.User);
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    [RequireBrowserOrigin]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status500InternalServerError, "application/problem+json")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(AuthenticationConstants.Scheme);
        return NoContent();
    }
}
