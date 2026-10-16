using System.Net;
using CloudRavel.Api.Middleware;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CloudRavel.Tests.Assessment;

public sealed class FunctionExceptionLoggingMiddlewareTests
{
    [Fact]
    public async Task Unhandled_exception_is_logged_and_returns_empty_500()
    {
        var capture = new CaptureLogger<FunctionExceptionLoggingMiddleware>();
        var middleware = new FunctionExceptionLoggingMiddleware(capture);
        var context = new TestFunctionContext();
        context.SetRequest(new FakeHttpRequestData(context, "GET", tenantIdHeaders: null,
            url: "http://localhost:7071/api/bot-monitor/ops/bots"));

        FunctionExecutionDelegate next = _ => throw new InvalidOperationException("integration-boom");

        await middleware.Invoke(context, next);

        Assert.Contains(capture.Entries, e => e.Level == LogLevel.Error && e.Exception != null);
        var response = context.GetInvocationResult();
        Assert.NotNull(response);
        Assert.Equal(HttpStatusCode.InternalServerError, response!.StatusCode);
        var body = await response.ReadBodyAsync();
        Assert.True(string.IsNullOrEmpty(body));
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, exception, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
