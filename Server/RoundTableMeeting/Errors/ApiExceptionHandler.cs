using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using RoundTableMeeting.Domain.Exceptions;

namespace RoundTableMeeting.Errors;

public sealed class ApiExceptionHandler(
    ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            return false;
        }

        var traceId = TraceIdResolver.Resolve(httpContext);
        logger.LogError(exception, "Unhandled request exception. TraceId: {TraceId}", traceId);

        var (status, code, title, detail) = exception switch
        {
            ConcurrencyConflictException => (StatusCodes.Status409Conflict, "concurrency_conflict", "Concurrency conflict", "The requested change could not be saved."),
            _ => (StatusCodes.Status500InternalServerError, "internal_error", "Internal server error", "An unexpected error occurred.")
        };

        var problem = new ApiProblemDetails
        {
            Status = status,
            Type = $"/problems/{code}",
            Title = title,
            Detail = detail,
            Instance = httpContext.Request.Path,
            Code = code,
            TraceId = traceId
        };

        httpContext.Response.StatusCode = status;
        await Results.Json(problem, contentType: "application/problem+json").ExecuteAsync(httpContext);
        return true;
    }
}
