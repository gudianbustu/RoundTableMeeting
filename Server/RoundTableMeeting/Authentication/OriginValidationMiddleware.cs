using Microsoft.Extensions.Options;
using System.Net.Http.Headers;

namespace RoundTableMeeting.Authentication;

public sealed class OriginValidationMiddleware(RequestDelegate next, IOptions<AuthenticationOptions> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<RequireBrowserOriginAttribute>() is null)
        {
            await next(context);
            return;
        }

        var origin = context.Request.Headers.Origin.ToString();
        var allowed = options.Value.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase);
        var metadata = endpoint.Metadata.GetMetadata<RequireBrowserOriginAttribute>()!;
        var contentTypeValid = context.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)
            && (!metadata.RequireJsonBody || IsJsonContentType(context.Request.ContentType));
        if (!allowed)
        {
            await AuthenticationProblemDetailsExtensions.WriteAsync(context, StatusCodes.Status403Forbidden, "origin_not_allowed");
            return;
        }

        if (!contentTypeValid)
        {
            await AuthenticationProblemDetailsExtensions.WriteAsync(context, StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type");
            return;
        }

        await next(context);
    }

    private static bool IsJsonContentType(string? contentType)
        => MediaTypeHeaderValue.TryParse(contentType, out var parsed)
            && string.Equals(parsed.MediaType, "application/json", StringComparison.OrdinalIgnoreCase);
}
