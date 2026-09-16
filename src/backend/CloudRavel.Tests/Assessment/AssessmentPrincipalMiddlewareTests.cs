using System.Net;
using CloudRavel.Api.Middleware;
using CloudRavel.Core.Interfaces;
using CloudRavel.Core.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;
using Xunit;

namespace CloudRavel.Tests.Assessment;

/// <summary>
/// Server-side enforcement tests for AssessmentPrincipalMiddleware (the
/// read-only service-credential path used by the standalone
/// cloudravel-assessment tool). Exercises the real middleware against the
/// fake FunctionContext/HttpRequestData harness — no live HTTP host.
/// </summary>
public sealed class AssessmentPrincipalMiddlewareTests
{
    private static readonly Guid TenantId = Guid.Parse("7a5e0c2e-1111-4c2e-9e01-00000000e501");

    private static readonly Guid CredentialId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private readonly FakeAuditRepository _auditRepo = new();
    private bool _nextCalled;

    private List<AuditEvent> _auditLog => _auditRepo.Events;

    // --- helpers -------------------------------------------------------------

    private AssessmentPrincipalMiddleware CreateMiddleware(Func<AuditEvent, Task>? auditOverride = null) =>
        new(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AssessmentPrincipalMiddleware>.Instance,
            new AssessmentPrincipalRateLimiter(),
            new FakeAuditRepository(_auditRepo, auditOverride));

    private async Task<HttpResponseData?> InvokeAsync(
        string method,
        string[]? tenantHeaders,
        string systemRole = AssessmentPrincipalMiddleware.ServicePrincipalRole,
        Func<AuditEvent, Task>? auditOverride = null)
    {
        _nextCalled = false;
        var context = new TestFunctionContext();
        context.Items["SystemRole"] = systemRole;
        context.SetUser(CredentialId);
        context.SetRequest(new FakeHttpRequestData(context, method, tenantHeaders));

        FunctionExecutionDelegate next = _ =>
        {
            _nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = CreateMiddleware(auditOverride);
        await middleware.Invoke(context, next);

        return context.GetInvocationResult();
    }

    private static void AssertForbidden(HttpResponseData? response, string expectedCode)
    {
        Assert.NotNull(response);
        Assert.Equal(403, (int)response!.StatusCode);
        var body = response.ReadBodyAsync().GetAwaiter().GetResult();
        Assert.Contains(expectedCode, body);
    }

    // --- non-GET refusal -------------------------------------------------------

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Non_get_is_refused_read_only_and_audited_as_mutation_blocked(string method)
    {
        var response = await InvokeAsync(method, new[] { TenantId.ToString() });

        AssertForbidden(response, "READ_ONLY_CREDENTIAL");
        var audit = Assert.Single(_auditLog);
        Assert.Equal("assessment.mutation_blocked", audit.Action);
        Assert.Equal(CredentialId, audit.UserId);
        Assert.Equal(method, audit.UserAgent!.Split(';')[1]);
        Assert.False(_nextCalled, "the handler must never run for a mutation");
    }

    // --- tenant scope ----------------------------------------------------------

    public static TheoryData<string?[]> InvalidTenantScopes => new()
    {
        new string?[] { null },                                              // header absent
        new string?[] { TenantId.ToString(), TenantId.ToString() },          // multiple values
        new string?[] { "*" },                                               // wildcard
        new string?[] { Guid.Empty.ToString() },                             // Guid.Empty
        new string?[] { "not-a-guid" },                                      // unparseable
    };

    [Theory]
    [MemberData(nameof(InvalidTenantScopes))]
    public async Task Reads_without_exactly_one_tenant_are_refused(string?[] headers)
    {
        var response = await InvokeAsync("GET", headers);

        AssertForbidden(response, "TENANT_SCOPE_REQUIRED");
        Assert.Empty(_auditLog);
        Assert.False(_nextCalled);
    }

    // --- happy path --------------------------------------------------------------

    [Fact]
    public async Task Valid_single_tenant_get_is_audited_and_passes_through()
    {
        var response = await InvokeAsync("GET", new[] { TenantId.ToString() });

        Assert.Null(response); // no short-circuit response; request passed through
        Assert.True(_nextCalled);
        var audit = Assert.Single(_auditLog);
        Assert.Equal("assessment.read", audit.Action);
        Assert.Equal(CredentialId, audit.UserId);
        Assert.Equal(TenantId, audit.TenantId);
    }

    [Fact]
    public async Task Human_roles_are_not_subject_to_the_assessment_path()
    {
        var response = await InvokeAsync("POST", tenantHeaders: null, systemRole: "system_admin");

        Assert.Null(response);
        Assert.True(_nextCalled);
        Assert.Empty(_auditLog);
    }

    // --- fail-closed audit ---------------------------------------------------------

    [Fact]
    public async Task Audit_write_failure_fails_the_read()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InvokeAsync("GET", new[] { TenantId.ToString() },
                auditOverride: _ => throw new InvalidOperationException("audit sink down")));

        Assert.Contains("audit sink down", ex.Message);
        Assert.False(_nextCalled, "fail-closed: no read may pass an unrecorded audit");
    }

    /// <summary>
    /// In-memory IAuditRepository: records into a shared list; optional override
    /// stands in for an audit sink failure. Every instance the middleware may
    /// receive writes into the same Events list via the sink delegate.
    /// </summary>
    private sealed class FakeAuditRepository : IAuditRepository
    {
        private readonly FakeAuditRepository? _shared;
        private readonly Func<AuditEvent, Task>? _override;

        public FakeAuditRepository(FakeAuditRepository shared, Func<AuditEvent, Task>? auditOverride = null)
        {
            _shared = shared;
            _override = auditOverride;
        }

        public FakeAuditRepository() { }

        public List<AuditEvent> Events => _shared?.Events ?? _events;
        private readonly List<AuditEvent> _events = new();

        public Task LogAsync(AuditEvent auditEvent)
        {
            if (_override != null) return _override(auditEvent);
            Events.Add(auditEvent);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditEvent>> GetByTenantAsync(Guid tenantId, int offset = 0, int limit = 50) =>
            throw new NotSupportedException("Not needed for middleware tests.");

        public Task<IReadOnlyList<AuditEvent>> GetByUserAsync(Guid userId, int offset = 0, int limit = 50) =>
            throw new NotSupportedException("Not needed for middleware tests.");
    }
}
