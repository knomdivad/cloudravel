using System.Net;
using System.Security.Claims;
using System.Text;
using Azure.Core.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.DependencyInjection;

namespace CloudRavel.Tests.TestSupport;

/// <summary>
/// Minimal in-memory stand-ins for the isolated-worker HTTP types.
///
/// The worker SDK ships no test doubles, and its request/response types are
/// abstract with no public constructors, so authorization guards that return an
/// <see cref="HttpResponseData"/> cannot be exercised without these. They
/// implement only what the guards under test touch: context items, request
/// headers, and a readable response body.
/// </summary>
public sealed class TestFunctionContext : FunctionContext
{
    public override string InvocationId { get; } = Guid.NewGuid().ToString();
    public override string FunctionId { get; } = "test-function";
    public override TraceContext TraceContext { get; } = null!;
    public override BindingContext BindingContext { get; } = null!;
    public override RetryContext RetryContext { get; } = null!;
    // WriteAsJsonAsync resolves its ObjectSerializer from WorkerOptions through
    // this provider, so the guards cannot write a body without it.
    public override IServiceProvider InstanceServices { get; set; } = BuildServices();
    public override FunctionDefinition FunctionDefinition { get; } = null!;
    public override IDictionary<object, object> Items { get; set; } = new Dictionary<object, object>();
    public override IInvocationFeatures Features { get; } = null!;

    private static IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.Configure<WorkerOptions>(options => options.Serializer = new JsonObjectSerializer());
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Populates the items TenantContextMiddleware would have set on a real request.
    /// Pass null for a role the middleware never resolved, which is how an
    /// unauthorized caller reaches a handler.
    /// </summary>
    public static TestFunctionContext With(
        string? systemRole = null, string? orgRole = null, Guid? tenantId = null)
    {
        var context = new TestFunctionContext();
        if (systemRole != null) context.Items["SystemRole"] = systemRole;
        if (orgRole != null) context.Items["OrgRole"] = orgRole;
        if (tenantId.HasValue) context.Items["TenantId"] = tenantId.Value;
        return context;
    }
}

public sealed class TestHttpRequestData : HttpRequestData
{
    public TestHttpRequestData(FunctionContext context, string method = "GET", string url = "https://localhost/api/test")
        : base(context)
    {
        Method = method;
        Url = new Uri(url);
    }

    public override Stream Body { get; } = new MemoryStream();
    public override HttpHeadersCollection Headers { get; } = new();
    public override IReadOnlyCollection<IHttpCookie> Cookies { get; } = Array.Empty<IHttpCookie>();
    public override Uri Url { get; }
    public override IEnumerable<ClaimsIdentity> Identities { get; } = Array.Empty<ClaimsIdentity>();
    public override string Method { get; }

    public override HttpResponseData CreateResponse() => new TestHttpResponseData(FunctionContext);
}

public sealed class TestHttpResponseData : HttpResponseData
{
    public TestHttpResponseData(FunctionContext context) : base(context)
    {
    }

    public override HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
    public override HttpHeadersCollection Headers { get; set; } = new();
    public override Stream Body { get; set; } = new MemoryStream();
    public override HttpCookies Cookies { get; } = new TestHttpCookies();
}

/// <summary>No guard under test sets cookies; this exists only to satisfy the base type.</summary>
public sealed class TestHttpCookies : HttpCookies
{
    public override void Append(string name, string value)
    {
    }

    public override void Append(IHttpCookie cookie)
    {
    }

    public override IHttpCookie CreateNew() => throw new NotSupportedException();
}

public static class TestHttpExtensions
{
    /// <summary>Reads a response body written by WriteAsJsonAsync.</summary>
    public static string ReadBody(this HttpResponseData response)
    {
        response.Body.Position = 0;
        using var reader = new StreamReader(response.Body, Encoding.UTF8, leaveOpen: true);
        return reader.ReadToEnd();
    }
}
