namespace RoundTableMeeting.Authentication;

/// <summary>
/// 为认证响应添加禁止缓存的响应头
/// </summary>
public sealed class AuthenticationResponseHeadersMiddleware(RequestDelegate next)
{
    /// <summary>
    /// 执行来源校验和后续中间件调用
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/api/auth", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.Pragma = "no-cache";
                return Task.CompletedTask;
            });
        }
        await next(context);
    }
}
