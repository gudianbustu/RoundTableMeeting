using System.Security.Claims;
using RoundTableMeeting.Abstraction.Services;

namespace RoundTableMeeting.Authentication;

/// <summary>
/// 从 HTTP 上下文读取当前认证用户
/// </summary>
public sealed class HttpCurrentUserContext(IHttpContextAccessor accessor) : ICurrentUserContext
{
    /// <summary>
    /// 获取当前认证主体，在请求之外返回空主体
    /// </summary>
    private ClaimsPrincipal User => accessor.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());

    /// <summary>
    /// 获取当前主体是否已认证且包含有效用户标识
    /// </summary>
    public bool IsAuthenticated => User.Identity?.IsAuthenticated == true && UserId.HasValue;

    /// <summary>
    /// 获取声明中的用户标识
    /// </summary>
    public Guid? UserId => Guid.TryParse(User.FindFirstValue(AuthenticationConstants.ClaimType), out var id) ? id : null;
}
