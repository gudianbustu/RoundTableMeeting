using RoundTableMeeting.Errors;
using Microsoft.AspNetCore.Mvc;

namespace RoundTableMeeting.Authentication;

public static class AuthenticationProblemDetailsExtensions
{
    public static ApiProblemDetails Create(HttpContext context, int status, string code, IReadOnlyDictionary<string, string[]>? errors = null)
        => new()
        {
            Status = status,
            Type = $"/problems/{code}",
            Title = code,
            Detail = code switch
            {
                "invalid_credentials" => "The username or password is invalid.",
                "origin_not_allowed" => "The browser origin is not allowed.",
                "unsupported_media_type" => "The request media type is not supported.",
                "validation_failed" => "One or more validation errors occurred.",
                "forbidden" => "You do not have permission to access this resource.",
                _ => "Authentication is required."
            },
            Instance = context.Request.Path,
            Code = code,
            TraceId = TraceIdResolver.Resolve(context),
            Errors = errors
        };

    public static IActionResult Result(HttpContext context, int status, string code, IReadOnlyDictionary<string, string[]>? errors = null)
        => new ObjectResult(Create(context, status, code, errors))
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" }
        };

    public static Task WriteAsync(HttpContext context, int status, string code)
    {
        var problem = Create(context, status, code);
        context.Response.StatusCode = status;
        return Results.Json(problem, contentType: "application/problem+json").ExecuteAsync(context);
    }
}
