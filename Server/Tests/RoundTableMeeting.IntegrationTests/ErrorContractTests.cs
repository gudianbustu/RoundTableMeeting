using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
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
