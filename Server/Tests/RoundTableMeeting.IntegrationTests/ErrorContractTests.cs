using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Logging;
using RoundTableMeeting.Authentication;
using RoundTableMeeting.Errors;

namespace RoundTableMeeting.IntegrationTests;

public sealed class ErrorContractTests
{
    [Fact]
    public void TraceIdFallsBackToHttpContextWhenActivityIsMissing()
    {
        var previous = Activity.Current;
        Activity.Current = null;
        try
        {
            var context = new DefaultHttpContext { TraceIdentifier = "fallback-trace-id" };

            Assert.Equal("fallback-trace-id", TraceIdResolver.Resolve(context));
        }
        finally
        {
            Activity.Current = previous;
        }
    }

    [Fact]
    public async Task CallerCancellationIsNotConvertedToProblemDetails()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var context = new DefaultHttpContext { RequestAborted = cancellation.Token };
        context.Response.Body = new MemoryStream();
        var logger = new CapturingLogger<ApiExceptionHandler>();
        var handler = new ApiExceptionHandler(logger);

        var handled = await handler.TryHandleAsync(
            context,
            new OperationCanceledException(cancellation.Token),
            CancellationToken.None);

        Assert.False(handled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
        Assert.DoesNotContain(logger.Levels, level => level >= LogLevel.Error);
    }

    [Fact]
    public void RequireBrowserOriginIsMethodOnlyAndNonRepeatable()
    {
        var usage = typeof(RequireBrowserOriginAttribute).GetCustomAttributes(typeof(AttributeUsageAttribute), inherit: false)
            .Cast<AttributeUsageAttribute>()
            .Single();

        Assert.Equal(AttributeTargets.Method, usage.ValidOn);
        Assert.False(usage.AllowMultiple);
        Assert.True(usage.Inherited);
    }

    [Fact]
    public void AllProblemDetailsResponsesShareErrorsWriterBoundary()
    {
        var context = new DefaultHttpContext { TraceIdentifier = "boundary-trace-id" };
        var validation = new ModelStateDictionary();
        validation.AddModelError("Name", "Name is required.");

        var validationResult = Assert.IsType<ObjectResult>(ApiProblemDetailsWriter.CreateValidationResult(context, validation));
        var authenticationResult = Assert.IsType<ObjectResult>(ApiProblemDetailsWriter.CreateResult(context, StatusCodes.Status401Unauthorized, "unauthenticated"));
        var originResult = Assert.IsType<ObjectResult>(ApiProblemDetailsWriter.CreateResult(context, StatusCodes.Status403Forbidden, "origin_not_allowed"));
        var mediaTypeResult = Assert.IsType<ObjectResult>(ApiProblemDetailsWriter.CreateResult(context, StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type"));

        Assert.Equal(StatusCodes.Status400BadRequest, validationResult.StatusCode);
        Assert.Equal(StatusCodes.Status401Unauthorized, authenticationResult.StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, originResult.StatusCode);
        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, mediaTypeResult.StatusCode);
        var expected = new[]
        {
            (Result: validationResult, Status: StatusCodes.Status400BadRequest, Code: "validation_failed", Detail: "One or more validation errors occurred."),
            (Result: authenticationResult, Status: StatusCodes.Status401Unauthorized, Code: "unauthenticated", Detail: "Authentication is required."),
            (Result: originResult, Status: StatusCodes.Status403Forbidden, Code: "origin_not_allowed", Detail: "The browser origin is not allowed."),
            (Result: mediaTypeResult, Status: StatusCodes.Status415UnsupportedMediaType, Code: "unsupported_media_type", Detail: "The request media type is not supported.")
        };

        Assert.All(expected, item =>
        {
            Assert.Contains("application/problem+json", item.Result.ContentTypes);
            var problem = Assert.IsType<ApiProblemDetails>(item.Result.Value);
            Assert.Equal(item.Status, item.Result.StatusCode);
            Assert.Equal(item.Status, problem.Status);
            Assert.Equal(item.Code, problem.Code);
            Assert.Equal($"/problems/{item.Code}", problem.Type);
            Assert.Equal(item.Detail, problem.Detail);
            Assert.Equal("boundary-trace-id", problem.TraceId);
        });
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Levels.Add(logLevel);

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }
}
