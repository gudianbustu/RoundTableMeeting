using Microsoft.AspNetCore.Mvc;

namespace RoundTableMeeting.Errors;

public static class ApiProblemDetailsWriter
{
    public static IActionResult CreateResult(HttpContext context, int status, string code, IReadOnlyDictionary<string, string[]>? errors = null)
        => new ObjectResult(ProblemDetailsFactory.Create(context, status, code, errors))
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" }
        };

    public static IActionResult CreateValidationResult(HttpContext context, Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary modelState)
        => new ObjectResult(ProblemDetailsFactory.CreateValidation(context, modelState))
        {
            StatusCode = StatusCodes.Status400BadRequest,
            ContentTypes = { "application/problem+json" }
        };

    public static Task WriteAsync(HttpContext context, int status, string code, IReadOnlyDictionary<string, string[]>? errors = null)
    {
        var problem = ProblemDetailsFactory.Create(context, status, code, errors);
        context.Response.StatusCode = status;
        return Results.Json(problem, contentType: "application/problem+json").ExecuteAsync(context);
    }
}
