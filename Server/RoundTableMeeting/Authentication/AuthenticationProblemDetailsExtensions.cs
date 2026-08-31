using RoundTableMeeting.Errors;
using Microsoft.AspNetCore.Mvc;

namespace RoundTableMeeting.Authentication;

/// <summary>
/// 提供认证问题详情响应的便捷适配器
/// </summary>
public static class AuthenticationProblemDetailsExtensions
{
    /// <summary>
    /// 创建统一的问题详情或领域对象
    /// </summary>
    public static ApiProblemDetails Create(HttpContext context, int status, string code, IReadOnlyDictionary<string, string[]>? errors = null)
        => ProblemDetailsFactory.Create(context, status, code, errors);

    /// <summary>
    /// 创建包含问题详情的 MVC 结果
    /// </summary>
    public static IActionResult Result(HttpContext context, int status, string code, IReadOnlyDictionary<string, string[]>? errors = null)
        => ApiProblemDetailsWriter.CreateResult(context, status, code, errors);

    /// <summary>
    /// 将问题详情异步写入 HTTP 响应
    /// </summary>
    public static Task WriteAsync(HttpContext context, int status, string code)
        => ApiProblemDetailsWriter.WriteAsync(context, status, code);
}
