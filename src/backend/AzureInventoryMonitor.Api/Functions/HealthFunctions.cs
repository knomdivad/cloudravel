using System.Net;
using AzureInventoryMonitor.Core.Interfaces;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace AzureInventoryMonitor.Api.Functions;

/// <summary>
/// Health check endpoint for monitoring and load balancer probes.
/// Returns component-level health status.
/// </summary>
public sealed class HealthFunctions
{
    private readonly ITenantDbConnectionFactory _connectionFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<HealthFunctions> _logger;

    public HealthFunctions(
        ITenantDbConnectionFactory connectionFactory,
        IConfiguration config,
        ILogger<HealthFunctions> logger)
    {
        _connectionFactory = connectionFactory;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/health
    /// Returns overall health status and component checks.
    /// No authentication required — used by load balancers and monitoring.
    /// </summary>
    [Function("HealthCheck")]
    public async Task<HttpResponseData> Health(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequestData req)
    {
        var checks = new Dictionary<string, ComponentHealth>();
        var overallHealthy = true;

        // Check database connectivity
        try
        {
            await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync();
            checks["database"] = new ComponentHealth("healthy", "SQL connection successful");
        }
        catch (Exception ex)
        {
            checks["database"] = new ComponentHealth("unhealthy", ex.Message);
            overallHealthy = false;
            _logger.LogError(ex, "Health check failed: database");
        }

        // Check configuration completeness
        var requiredConfig = new[] { "AzureAd:TenantId", "AzureAd:ClientId" };
        var missingConfig = requiredConfig.Where(k => string.IsNullOrEmpty(_config[k])).ToList();
        if (missingConfig.Count == 0)
        {
            checks["configuration"] = new ComponentHealth("healthy", "All required config present");
        }
        else
        {
            checks["configuration"] = new ComponentHealth("degraded", $"Missing: {string.Join(", ", missingConfig)}");
        }

        var status = overallHealthy ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable;
        var response = req.CreateResponse(status);
        await response.WriteAsJsonAsync(new
        {
            status = overallHealthy ? "healthy" : "unhealthy",
            timestamp = DateTime.UtcNow,
            version = typeof(HealthFunctions).Assembly.GetName().Version?.ToString() ?? "1.0.0",
            checks
        });
        return response;
    }

    /// <summary>
    /// GET /api/health/ready
    /// Readiness probe — checks if the app is ready to serve traffic.
    /// </summary>
    [Function("ReadinessProbe")]
    public async Task<HttpResponseData> Ready(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health/ready")] HttpRequestData req)
    {
        try
        {
            await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM tenants";
            await cmd.ExecuteScalarAsync();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { status = "ready" });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Readiness probe failed");
            var response = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            await response.WriteAsJsonAsync(new { status = "not_ready", error = ex.Message });
            return response;
        }
    }

    private sealed record ComponentHealth(string Status, string? Detail);
}
