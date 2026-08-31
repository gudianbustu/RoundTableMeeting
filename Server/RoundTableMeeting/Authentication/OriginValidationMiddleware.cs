using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;

namespace RoundTableMeeting.Authentication;

/// <summary>
/// 校验浏览器来源和请求媒体类型
/// </summary>
public sealed class OriginValidationMiddleware(RequestDelegate next, IOptions<AuthenticationOptions> options)
{
    /// <summary>
    /// 执行来源校验和后续中间件调用
    /// </summary>
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

    /// <summary>
    /// 校验请求是否使用 JSON 媒体类型
    /// </summary>
    private static bool IsJsonContentType(string? contentType)
        => MediaTypeHeaderValue.TryParse(contentType, out var parsed)
            && string.Equals(parsed.MediaType, "application/json", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 校验唯一 Origin 请求头是否在白名单中
    /// </summary>
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
