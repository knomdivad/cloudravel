using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace CloudRavel.Api.Middleware;

/// <summary>
/// Logs unhandled exceptions from HTTP function invocations at Error level and returns
/// an empty 500 response so details are not leaked to clients.
/// </summary>
public sealed class FunctionExceptionLoggingMiddleware : IFunctionsWorkerMiddleware
{
    private readonly ILogger<FunctionExceptionLoggingMiddleware> _logger;

    public FunctionExceptionLoggingMiddleware(ILogger<FunctionExceptionLoggingMiddleware> logger) =>
        _logger = logger;

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unhandled exception in function {FunctionId}",
                context.FunctionId);

            var httpRequestData = await context.GetHttpRequestDataAsync();
            if (httpRequestData != null)
            {
                var response = httpRequestData.CreateCorsResponse(HttpStatusCode.InternalServerError);
                context.GetInvocationResult().Value = response;
                return;
            }

            throw;
        }
    }
}
