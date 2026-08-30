using RoundTableMeeting.Errors;
using Microsoft.AspNetCore.Mvc;

namespace RoundTableMeeting.Authentication;

public static class AuthenticationProblemDetailsExtensions
{
    public static ApiProblemDetails Create(HttpContext context, int status, string code, IReadOnlyDictionary<string, string[]>? errors = null)
        => ProblemDetailsFactory.Create(context, status, code, errors);

    public static IActionResult Result(HttpContext context, int status, string code, IReadOnlyDictionary<string, string[]>? errors = null)
        => ApiProblemDetailsWriter.CreateResult(context, status, code, errors);

    public static Task WriteAsync(HttpContext context, int status, string code)
        => ApiProblemDetailsWriter.WriteAsync(context, status, code);
}
