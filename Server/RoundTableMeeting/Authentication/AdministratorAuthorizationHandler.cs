using Microsoft.AspNetCore.Authorization;
using RoundTableMeeting.Abstraction.Services;

namespace RoundTableMeeting.Authentication;

public sealed class AdministratorRequirement : IAuthorizationRequirement;

public sealed class AdministratorAuthorizationHandler(IAdministratorAccessService accessService, IHttpContextAccessor accessor)
    : AuthorizationHandler<AdministratorRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, AdministratorRequirement requirement)
    {
        if (await accessService.IsAdministratorAsync(accessor.HttpContext?.RequestAborted ?? CancellationToken.None))
            context.Succeed(requirement);
    }
}
