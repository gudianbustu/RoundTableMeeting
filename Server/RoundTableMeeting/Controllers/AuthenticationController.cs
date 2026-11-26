using System.Security.Claims;
using RoundTableMeeting.Abstraction.Contracts.UserManagement;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using RoundTableMeeting.Abstraction.Contracts.Authentication;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Authentication;
using RoundTableMeeting.Errors;
using AppAuthenticationService = RoundTableMeeting.Abstraction.Services.IAuthenticationService;

namespace RoundTableMeeting.Controllers;

/// <summary>
/// 处理登录、当前用户和退出登录请求
/// </summary>
[ApiController]
[Route("api/auth")]
[NoStoreResponse]
public sealed class AuthenticationController(AppAuthenticationService authenticationService) : ControllerBase
{
    /// <summary>
    /// 校验凭据并签发认证 Cookie
    /// </summary>
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
        return await SignInUser(result.User!);
    }

    [HttpPost("register")]
    [AllowAnonymous]
    [RequireBrowserOrigin(true)]
    [ProducesResponseType(typeof(CurrentUserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(ApiProblemDetails), StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Register(LoginRequest request, [FromServices] IUserManagementService accounts, CancellationToken cancellationToken)
    {
        var result = await accounts.RegisterAsync(request.Username!, request.Password!, cancellationToken);
        if (result.Status == UserManagementStatus.UsernameConflict)
            return ApiProblemDetailsWriter.CreateResult(HttpContext, StatusCodes.Status409Conflict, "username_conflict");
        if (result.Status == UserManagementStatus.ValidationFailed)
            return ApiProblemDetailsWriter.CreateResult(HttpContext, StatusCodes.Status400BadRequest, "validation_failed", result.Errors);
        var user = result.User!;
        return await SignInUser(new CurrentUserDto(user.UserId, user.Username, user.Nickname, user.IsAdmin));
    }

    private async Task<IActionResult> SignInUser(CurrentUserDto user)
    {
        await HttpContext.SignInAsync(AuthenticationConstants.Scheme, new ClaimsPrincipal(new ClaimsIdentity([new Claim(AuthenticationConstants.ClaimType, user.UserId.ToString())], AuthenticationConstants.Scheme)), new AuthenticationProperties { IsPersistent = false, AllowRefresh = false });
        return Ok(user);
    }

    /// <summary>
    /// 返回当前认证 Cookie 对应的用户
    /// </summary>
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

    /// <summary>
    /// 清除当前认证 Cookie
    /// </summary>
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
