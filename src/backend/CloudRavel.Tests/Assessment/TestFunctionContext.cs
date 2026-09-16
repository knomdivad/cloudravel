using System.Net;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Context.Features;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.DependencyInjection;

namespace CloudRavel.Tests.Assessment;

/// <summary>
/// Minimal in-memory harness for exercising IFunctionsWorkerMiddleware
/// implementations without the Azure Functions host. Built directly on the
/// worker SDK's public seams:
///
///   - <see cref="FunctionContext"/> is public-abstract, so the harness subclasses it.
///   - GetHttpRequestDataAsync() resolves IHttpRequestDataFeature (public) from
///     Features — the harness registers one returning the fake request.
///   - context.GetInvocationResult() resolves the INTERNAL IFunctionBindingsFeature
///     from Features; the worker SDK's own internal InvocationFeatures
///     implementation is instantiated via reflection so internal types never
///     appear in this assembly.
///   - GetHttpContext() (AspNetCore integration) reads Items["HttpRequestContext"].
/// </summary>
public sealed class TestFunctionContext : FunctionContext
{
    // AspNetCore integration: Constants.HttpContextKey (internal) — value is "HttpRequestContext".
    private const string HttpContextItemsKey = "HttpRequestContext";

    private static readonly Type BindingsFeatureInterface =
        typeof(HttpRequestData).Assembly.GetType(
            "Microsoft.Azure.Functions.Worker.Context.Features.IFunctionBindingsFeature", throwOnError: true)!;

    private static readonly Type InvocationFeaturesType =
        typeof(HttpRequestData).Assembly.GetType(
            "Microsoft.Azure.Functions.Worker.InvocationFeatures", throwOnError: true)!;

    private static readonly MethodInfo GetFeatureMethod = InvocationFeaturesType
        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .Single(m => m.Name == "Get" && m.IsGenericMethod);

    private static readonly MethodInfo SetFeatureMethod = InvocationFeaturesType
        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .Single(m => m.Name == "Set" && m.IsGenericMethod);

    private readonly object _features; // real worker InvocationFeatures (internal), driven via reflection

    public TestFunctionContext()
    {
        // InvocationFeatures(IEnumerable<IInvocationFeatureProvider>) — pass an
        // empty provider array; every feature is registered explicitly via Set<T>.
        // IInvocationFeatureProvider lives in the Worker.Core assembly
        // (namespace Microsoft.Azure.Functions.Worker).
        var providerType = typeof(TraceContext).Assembly.GetType(
            "Microsoft.Azure.Functions.Worker.IInvocationFeatureProvider",
            throwOnError: true)!;
        var featureProviders = Array.CreateInstance(providerType, 0);
        _features = Activator.CreateInstance(InvocationFeaturesType, new object[] { featureProviders })
            ?? throw new InvalidOperationException("Could not construct worker InvocationFeatures.");

        SetFeature(BindingsFeatureInterface, BindingsDispatchProxy.Create());

        // GetUserId() reads the authenticated principal off the AspNetCore HttpContext.
        var httpContext = new DefaultHttpContext();
        Items[HttpContextItemsKey] = httpContext;
        CredentialUserId = Guid.NewGuid();
        SetUser(CredentialUserId);
    }

    public Guid CredentialUserId { get; }

    public override IDictionary<object, object> Items { get; set; } = new Dictionary<object, object>();

    public override IInvocationFeatures Features => (IInvocationFeatures)_features;

    public override string InvocationId => Guid.NewGuid().ToString();

    public override string FunctionId => "test-function";

    public override TraceContext TraceContext => new TestTraceContext();

    public override BindingContext BindingContext => throw new NotSupportedException("Not needed for middleware tests.");

    public override RetryContext RetryContext => throw new NotSupportedException("Not needed for middleware tests.");

    public override IServiceProvider InstanceServices { get; set; } = new ServiceProviderShim();

    /// <summary>
    /// InstanceServices stub satisfying the worker SDK's WriteAsJsonAsync path
    /// (GetObjectSerializer resolves IOptions&lt;WorkerOptions&gt;.Value.Serializer).
    /// Mirrors Program.cs's camelCase worker serializer configuration.
    /// </summary>
    private sealed class ServiceProviderShim : IServiceProvider
    {
        private readonly IServiceProvider _inner = new ServiceCollection()
            .AddOptions()
            .Configure<WorkerOptions>(options =>
            {
                var jsonOptions = new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                    PropertyNameCaseInsensitive = true,
                };
                options.Serializer = new Azure.Core.Serialization.JsonObjectSerializer(jsonOptions);
            })
            .BuildServiceProvider();

        public object? GetService(Type serviceType) => _inner.GetService(serviceType);
    }

    public override FunctionDefinition FunctionDefinition =>
        throw new NotSupportedException("Not needed for middleware tests.");

    public void SetRequest(HttpRequestData request)
    {
        SetFeature(
            typeof(HttpRequestData).Assembly.GetType(
                "Microsoft.Azure.Functions.Worker.Http.IHttpRequestDataFeature", throwOnError: true)!,
            new FixedRequestFeature(request));
    }

    public void SetUser(Guid userId)
    {
        var httpContext = (DefaultHttpContext)Items[HttpContextItemsKey];
        var identity = new ClaimsIdentity(authenticationType: "test");
        identity.AddClaim(new Claim("oid", userId.ToString()));
        httpContext.User = new ClaimsPrincipal(identity);
    }

    /// <summary>The response the middleware short-circuited with, if any.</summary>
    public HttpResponseData? GetInvocationResult()
    {
        // IFunctionBindingsFeature is internal to the worker assembly; the
        // InvocationResult member is reached by reflection on the feature
        // instance registered in the constructor.
        var bindings = GetFeatureMethod.MakeGenericMethod(BindingsFeatureInterface)
            .Invoke(_features, null);
        return bindings?.GetType().GetProperty("InvocationResult")?.GetValue(bindings) as HttpResponseData;
    }

    // IFunctionBindingsFeature is internal; BindingsFeatureStub implements the
    // member shape the middleware's GetInvocationResult() path needs
    // (InvocationResult get/set). Reflection-based registration erases the
    // compile-time type, so both write and read go through reflection.

    private void SetFeature(Type featureType, object instance) =>
        SetFeatureMethod.MakeGenericMethod(featureType).Invoke(_features, new[] { instance });

    private sealed class FixedRequestFeature : IHttpRequestDataFeature
    {
        private readonly HttpRequestData _request;
        public FixedRequestFeature(HttpRequestData request) => _request = request;
        public ValueTask<HttpRequestData?> GetHttpRequestDataAsync(FunctionContext context) =>
            ValueTask.FromResult<HttpRequestData?>(_request);
    }

    private sealed class TestTraceContext : TraceContext
    {
        public override string TraceParent => "00-test-trace-id-test-span-id-00";
        public override string TraceState => string.Empty;
    }

    /// <summary>IServiceProvider that yields null for everything (middleware under test resolves its deps via ctor).</summary>
    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    /// <summary>
    /// Stand-in for the internal IFunctionBindingsFeature. Implements the
    /// interface at runtime via a DispatchProxy so the internal-typed Set<T>
    /// registration succeeds and reads/writes of InvocationResult land here.
    /// </summary>
    private class BindingsDispatchProxy : DispatchProxy
    {
        private readonly Dictionary<string, object?> _state = new();

        public static object Create() =>
            DispatchProxy.Create(BindingsFeatureInterface, typeof(BindingsDispatchProxy))!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null) return null;
            var name = targetMethod.Name;
            if (name == "get_InvocationResult") return _state.TryGetValue("result", out var v) ? v : null;
            if (name == "set_InvocationResult") { _state["result"] = args![0]; return null; }
            if (name == "get_OutputBindingData") return new Dictionary<string, object?>();
            if (name == "get_TriggerMetadata") return new Dictionary<string, object?>();
            if (name == "get_InputData") return new Dictionary<string, object?>();
            return null;
        }
    }
}

/// <summary>
/// Bare HttpRequestData for middleware tests: canned method/headers/url; empty
/// body; CreateResponse yields a capturing FakeHttpResponseData.
/// </summary>
public sealed class FakeHttpRequestData : HttpRequestData
{
    public FakeHttpRequestData(
        FunctionContext functionContext,
        string method,
        string[]? tenantIdHeaders,
        string url = "http://localhost:7071/api/tenants")
        : base(functionContext)
    {
        Method = method;
        Url = new Uri(url);
        var headers = new HttpHeadersCollection();
        if (tenantIdHeaders != null)
        {
            foreach (var value in tenantIdHeaders)
                headers.Add("X-Tenant-Id", value);
        }
        Headers = headers;
    }

    public override Stream Body => Stream.Null;

    public override HttpHeadersCollection Headers { get; }

    public override IReadOnlyCollection<IHttpCookie> Cookies { get; } = new List<IHttpCookie>();

    public override Uri Url { get; }

    public override IEnumerable<ClaimsIdentity> Identities { get; } = new List<ClaimsIdentity>();

    public override string Method { get; }

    public override HttpResponseData CreateResponse() => new FakeHttpResponseData(this);
}

/// <summary>Minimal response capturing status code + body for assertions.</summary>
public sealed class FakeHttpResponseData : HttpResponseData
{
    public FakeHttpResponseData(HttpRequestData request)
        : base(request.FunctionContext)
    {
        StatusCode = HttpStatusCode.OK;
        Headers = new HttpHeadersCollection();
        Body = new MemoryStream();
    }

    public override HttpStatusCode StatusCode { get; set; }

    public override HttpHeadersCollection Headers { get; set; }

    public override Stream Body { get; set; }

    public override HttpCookies Cookies => throw new NotSupportedException("Not needed for middleware tests.");
}

internal static class FakeHttpResponseDataExtensions
{
    /// <summary>Reads the response body as text (position reset for repeat reads).</summary>
    public static async Task<string> ReadBodyAsync(this HttpResponseData response)
    {
        response.Body.Position = 0;
        using var reader = new StreamReader(response.Body, leaveOpen: true);
        var text = await reader.ReadToEndAsync();
        response.Body.Position = 0;
        return text;
    }
}
