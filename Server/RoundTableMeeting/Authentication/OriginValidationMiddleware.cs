using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;

namespace RoundTableMeeting.Authentication;

public sealed class OriginValidationMiddleware(RequestDelegate next, IOptions<AuthenticationOptions> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        var metadata = endpoint?.Metadata.GetMetadata<RequireBrowserOriginAttribute>();
        if (metadata is null)
        {
            await next(context);
            return;
        }

        var methodMetadata = endpoint!.Metadata.GetMetadata<IHttpMethodMetadata>();
        if (methodMetadata?.HttpMethods is { Count: > 0 } methods
            && !methods.Contains(context.Request.Method, StringComparer.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        if (!HasAllowedOrigin(context))
        {
            await RoundTableMeeting.Errors.ApiProblemDetailsWriter.WriteAsync(context, StatusCodes.Status403Forbidden, "origin_not_allowed");
            return;
        }

        if (metadata.RequireJsonBody && !IsJsonContentType(context.Request.ContentType))
        {
            await RoundTableMeeting.Errors.ApiProblemDetailsWriter.WriteAsync(context, StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type");
            return;
        }

        await next(context);
    }

    private static bool IsJsonContentType(string? contentType)
        => MediaTypeHeaderValue.TryParse(contentType, out var parsed)
            && string.Equals(parsed.MediaType, "application/json", StringComparison.OrdinalIgnoreCase);

    private bool HasAllowedOrigin(HttpContext context)
    {
        var values = context.Request.Headers.Origin;
        if (values.Count != 1) return false;

        var origin = values[0];
        if (string.IsNullOrWhiteSpace(origin) || string.Equals(origin.Trim(), "null", StringComparison.OrdinalIgnoreCase)) return false;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https")
            || string.IsNullOrEmpty(parsed.Host)
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || !string.IsNullOrEmpty(parsed.Query)
            || !string.IsNullOrEmpty(parsed.Fragment)
            || parsed.AbsolutePath != "/"
            || !string.Equals(origin, parsed.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return options.Value.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase);
    }
}
