using Microsoft.AspNetCore.Mvc.Filters;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Errors;

namespace RoundTableMeeting.RealtimeChat;

public sealed class MessageSendRateLimitFilter(
    MessageSendRateLimiter limiter,
    ICurrentUserContext currentUser,
    IMeetingRepository meetings,
    IMeetingParticipantRepository participants) : IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        if (!currentUser.IsAuthenticated
            || currentUser.UserId is not Guid userId
            || !Guid.TryParse(context.RouteData.Values["meetingId"]?.ToString(), out var meetingId))
        {
            await next();
            return;
        }

        var meeting = await meetings.FindActiveByIdAsync(meetingId, context.HttpContext.RequestAborted);
        var participant = meeting is null ? null : await participants.FindByUserAsync(meetingId, userId, context.HttpContext.RequestAborted);
        if (meeting is null || participant is null)
        {
            await next();
            return;
        }

        var source = context.HttpContext.Connection.RemoteIpAddress?.ToString();
        if (limiter.TryAcquire(userId, participant.ID, source, out var retryAfterSeconds))
        {
            await next();
            return;
        }

        context.HttpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.Result = ApiProblemDetailsWriter.CreateResult(context.HttpContext, StatusCodes.Status429TooManyRequests, "message_send_rate_limited");
    }
}
