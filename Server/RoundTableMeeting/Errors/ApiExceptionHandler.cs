using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using RoundTableMeeting.Domain.Exceptions;

namespace RoundTableMeeting.Errors;

/// <summary>
/// 将未处理异常转换为统一 API 错误响应
/// </summary>
public sealed class ApiExceptionHandler(
    ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    /// <summary>
    /// 记录异常并写入统一问题详情响应
    /// </summary>
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            return false;
        }

        logger.LogError(exception, "Unhandled request exception. TraceId: {TraceId}", TraceIdResolver.Resolve(httpContext));

        var (status, code) = exception switch
        {
            ConcurrencyConflictException => (StatusCodes.Status409Conflict, "concurrency_conflict"),
            UsernameConflictException => (StatusCodes.Status409Conflict, "username_conflict"),
            _ => (StatusCodes.Status500InternalServerError, "internal_error")
        };

        await ApiProblemDetailsWriter.WriteAsync(httpContext, status, code);
        return true;
    }
}
