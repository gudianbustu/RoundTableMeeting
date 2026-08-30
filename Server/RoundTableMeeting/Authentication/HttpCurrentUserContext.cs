using System.Security.Claims;
using RoundTableMeeting.Abstraction.Services;

namespace RoundTableMeeting.Authentication;

public sealed class HttpCurrentUserContext(IHttpContextAccessor accessor) : ICurrentUserContext
{
    private ClaimsPrincipal User => accessor.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());

    public bool IsAuthenticated => User.Identity?.IsAuthenticated == true && UserId.HasValue;

    public Guid? UserId => Guid.TryParse(User.FindFirstValue(AuthenticationConstants.ClaimType), out var id) ? id : null;
}
