using System.Collections.Concurrent;
using System.Net;
using CloudRavel.Core.Interfaces;
using CloudRavel.Core.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace CloudRavel.Api.Middleware;

/// <summary>
/// Read-only service-credential path for the standalone assessment tool
/// (repo cloudravel-assessment). The tool authenticates as a dedicated
/// service user whose global_role is 'service_principal' and which holds
/// only 'read_only' org grants — never cloud_admin/org_admin.
///
/// Enforcement, per request:
///   1. Exactly one tenant scope: a single X-Tenant-Id header is required
///      and no request may span or aggregate tenants (no wildcard scope).
///   2. Read-only: any non-GET (and any non-read method) is refused 403
///      before it reaches a handler — the credential cannot mutate anything.
///   3. Rate limited per credential (fixed window, default 60 req/min) — 429
///      with Retry-After when exhausted.
///   4. Audited: every read lands in the immutable audit_events log with the
///      credential identity, tenant scoped, and timestamp.
///
/// The credential itself is provisioned by the operator (users row +
/// read_only user_tenant_access grants); nothing here invents or stores
/// secrets. Server-side enforcement is the guarantee — the tool asserting
/// read-only intent is not the control.
/// </summary>
public sealed class AssessmentPrincipalMiddleware : IFunctionsWorkerMiddleware
{
    public const string ServicePrincipalRole = "service_principal";

    private readonly ILogger<AssessmentPrincipalMiddleware> _logger;
    private readonly AssessmentPrincipalRateLimiter _rateLimiter;
    private readonly IAuditRepository _auditRepo;

    public AssessmentPrincipalMiddleware(
        ILogger<AssessmentPrincipalMiddleware> logger,
        AssessmentPrincipalRateLimiter rateLimiter,
        IAuditRepository auditRepo)
    {
        _logger = logger;
        _rateLimiter = rateLimiter;
        _auditRepo = auditRepo;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var httpRequestData = await context.GetHttpRequestDataAsync();
        if (httpRequestData == null)
        {
            await next(context);
            return;
        }

        var path = httpRequestData.Url.AbsolutePath.ToLowerInvariant();
        if (path.Contains("/health") || path.Contains("/auth/login"))
        {
            await next(context);
            return;
        }

        // Only service principals are subject to this middleware; human callers
        // (system_admin / member with their own grants) pass straight through.
        var systemRole = context.Items.TryGetValue("SystemRole", out var r) && r is string s ? s : string.Empty;
        if (!string.Equals(systemRole, ServicePrincipalRole, StringComparison.Ordinal))
        {
            await next(context);
            return;
        }

        var userId = context.GetUserId();

        // Read-only: refuse every non-GET up front, before any handler runs.
        if (!httpRequestData.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            await AuditAsync(userId, httpRequestData, path, "assessment.mutation_blocked");
            _logger.LogWarning("Service principal {UserId} attempted non-read {Method} {Path} — refused",
                userId, httpRequestData.Method, path);
            context.GetInvocationResult().Value = await Forbidden(httpRequestData,
                "READ_ONLY_CREDENTIAL", "The assessment service credential is read-only; mutations are refused.");
            return;
        }

        // Single-tenant scope: exactly one X-Tenant-Id, no wildcard/all-tenants form.
        var hasHeader = httpRequestData.Headers.TryGetValues("X-Tenant-Id", out var values);
        var headerList = values?.ToList();
        if (!hasHeader || headerList is not { Count: 1 } || !Guid.TryParse(headerList[0], out var tenantId)
            || tenantId == Guid.Empty)
        {
            _logger.LogWarning("Service principal {UserId} request without a single tenant scope on {Path}",
                userId, path);
            context.GetInvocationResult().Value = await Forbidden(httpRequestData,
                "TENANT_SCOPE_REQUIRED",
                "Assessment reads must target exactly one workspace via a single X-Tenant-Id header.");
            return;
        }

        // Per-credential rate limit.
        if (!_rateLimiter.IsAllowed(userId.Value.ToString()))
        {
            var limited = httpRequestData.CreateCorsResponse((HttpStatusCode)429);
            limited.Headers.Add("Retry-After", "60");
            await limited.WriteAsJsonAsync(new
            {
                code = "RATE_LIMITED",
                message = "Assessment credential rate limit exceeded; retry in a minute."
            });
            context.GetInvocationResult().Value = limited;
            return;
        }

        // Every read is attributable: credential identity + tenant + timestamp.
        await AuditAsync(userId, httpRequestData, path, "assessment.read", tenantId);

        _logger.LogInformation("Assessment principal {UserId} read {Path} for tenant {TenantId}",
            userId, path, tenantId);
        await next(context);
    }

    private async Task AuditAsync(Guid? userId, HttpRequestData req, string path, string action, Guid? tenantId = null)
    {
        try
        {
            await _auditRepo.LogAsync(new AuditEvent
            {
                TenantId = tenantId,
                UserId = userId ?? Guid.Empty,
                Action = action,
                EntityType = "assessment",
                EntityId = tenantId?.ToString() ?? path,
                DetailsJson = null,
                ClientIp = req.Url.Host,
                UserAgent = $"assessment-principal;{req.Method};{path}"
            });
        }
        catch (Exception ex)
        {
            // Audit is the control — a failed audit write must not silently pass the read through.
            _logger.LogError(ex, "Failed to write assessment audit event for {Path}", path);
            throw;
        }
    }

    private static async Task<HttpResponseData> Forbidden(HttpRequestData req, string code, string message)
    {
        var response = req.CreateCorsResponse(HttpStatusCode.Forbidden);
        await response.WriteAsJsonAsync(new { code, message });
        return response;
    }
}

/// <summary>
/// Fixed-window rate limiter for assessment service credentials (per credential
/// id, default 60 requests/minute). In-process by design: the assessment path
/// is low-volume; a restart resets the window (documented operational trade-off).
/// </summary>
public sealed class AssessmentPrincipalRateLimiter
{
    private readonly ConcurrentDictionary<string, Window> _windows = new();
    private readonly int _maxRequests;
    private readonly TimeSpan _window;

    public AssessmentPrincipalRateLimiter(int maxRequests = 60, TimeSpan? window = null)
    {
        _maxRequests = maxRequests;
        _window = window ?? TimeSpan.FromMinutes(1);
    }

    public bool IsAllowed(string key)
    {
        var now = DateTime.UtcNow;
        var window = _windows.AddOrUpdate(
            key,
            _ => new Window(now, 1),
            (_, existing) =>
                now - existing.StartedAt >= _window ? new Window(now, 1) : existing with { Count = existing.Count + 1 });
        return window.Count <= _maxRequests;
    }

    private sealed record Window(DateTime StartedAt, int Count);
}
