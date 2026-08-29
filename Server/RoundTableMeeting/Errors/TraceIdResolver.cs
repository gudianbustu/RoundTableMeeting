using System.Diagnostics;

namespace RoundTableMeeting.Errors;

public static class TraceIdResolver
{
    public static string Resolve(HttpContext httpContext)
        => Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;
}
