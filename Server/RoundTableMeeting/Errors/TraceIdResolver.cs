using System.Diagnostics;

namespace RoundTableMeeting.Errors;

/// <summary>
/// 解析日志和错误响应使用的追踪标识
/// </summary>
public static class TraceIdResolver
{
    /// <summary>
    /// 解析当前请求的追踪标识
    /// </summary>
    public static string Resolve(HttpContext httpContext)
        => Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;
}
