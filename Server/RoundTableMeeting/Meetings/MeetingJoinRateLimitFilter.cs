using Microsoft.AspNetCore.Mvc.Filters;
using RoundTableMeeting.Errors;

namespace RoundTableMeeting.Meetings;

public sealed class MeetingJoinRateLimitFilter(MeetingJoinRateLimiter limiter) : IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        if (context.HttpContext.User.Identity?.IsAuthenticated != true)
        {
            await next();
            return;
        }
        var claim = context.HttpContext.User.FindFirst(Authentication.AuthenticationConstants.ClaimType)?.Value;
        if (!Guid.TryParse(claim, out var userId))
        {
            await next();
            return;
        }
        var source = context.HttpContext.Connection.RemoteIpAddress?.ToString();
        if (limiter.TryAcquire(userId, source, out var retryAfterSeconds))
        {
            await next();
            return;
        }
        context.HttpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.Result = ApiProblemDetailsWriter.CreateResult(context.HttpContext, StatusCodes.Status429TooManyRequests, "meeting_join_rate_limited");
    }
}
