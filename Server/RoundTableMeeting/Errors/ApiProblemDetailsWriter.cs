using Microsoft.AspNetCore.Mvc;

namespace RoundTableMeeting.Errors;

/// <summary>
/// 创建并写入统一的问题详情响应
/// </summary>
public static class ApiProblemDetailsWriter
{
    /// <summary>
    /// 创建问题详情 MVC 响应结果
    /// </summary>
    public static IActionResult CreateResult(HttpContext context, int status, string code, IReadOnlyDictionary<string, string[]>? errors = null)
        => new ObjectResult(ProblemDetailsFactory.Create(context, status, code, errors))
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" }
        };

    /// <summary>
    /// 创建模型校验失败的 MVC 响应结果
    /// </summary>
    public static IActionResult CreateValidationResult(HttpContext context, Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary modelState)
        => new ObjectResult(ProblemDetailsFactory.CreateValidation(context, modelState))
        {
            StatusCode = StatusCodes.Status400BadRequest,
            ContentTypes = { "application/problem+json" }
        };

    /// <summary>
    /// 将问题详情异步写入 HTTP 响应
    /// </summary>
    public static Task WriteAsync(HttpContext context, int status, string code, IReadOnlyDictionary<string, string[]>? errors = null)
    {
        var problem = ProblemDetailsFactory.Create(context, status, code, errors);
        context.Response.StatusCode = status;
        return Results.Json(problem, contentType: "application/problem+json").ExecuteAsync(context);
    }
}
