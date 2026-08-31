namespace RoundTableMeeting.Authentication;

/// <summary>
/// 为敏感 API 响应统一添加禁缓存头。
/// </summary>
public sealed class SensitiveResponseHeadersMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<NoStoreResponseAttribute>() is not null)
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.Remove(Microsoft.Net.Http.Headers.HeaderNames.CacheControl);
                context.Response.Headers[Microsoft.Net.Http.Headers.HeaderNames.CacheControl] = "no-store";
                context.Response.Headers[Microsoft.Net.Http.Headers.HeaderNames.Pragma] = "no-cache";
                return Task.CompletedTask;
            });
        }

        await next(context);
    }
}
