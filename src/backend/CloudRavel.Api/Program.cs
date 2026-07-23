using CloudRavel.Api.Middleware;
using CloudRavel.Core.Auth;
using CloudRavel.Core.Interfaces;
using CloudRavel.Infrastructure;
using CloudRavel.Infrastructure.AiOps;
using CloudRavel.Infrastructure.Auth;
using CloudRavel.Infrastructure.Azure;
using CloudRavel.Infrastructure.Data;
using CloudRavel.Infrastructure.MultiCloud;
using CloudRavel.Infrastructure.Queue;
using CloudRavel.Infrastructure.Secrets;
using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VaultSharp;
using VaultSharp.V1.AuthMethods;
using VaultSharp.V1.AuthMethods.Token;

// Enable Dapper snake_case → PascalCase column mapping
Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

// Register Dapper type handlers for JSON columns
Dapper.SqlMapper.AddTypeHandler(new JsonTypeHandler<Dictionary<string, string>>());
Dapper.SqlMapper.AddTypeHandler(new JsonTypeHandler<List<string>>());

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication(builder =>
    {
        // Isolated-worker functions don't get ASP.NET Core's UseAuthentication()/
        // UseAuthorization() pipeline or automatic [Authorize] enforcement —
        // AuthEnforcementMiddleware does it manually via HttpContext.AuthenticateAsync(),
        // then TenantContextMiddleware checks the authenticated user's tenant access.
        builder.UseMiddleware<AuthEnforcementMiddleware>();
        builder.UseMiddleware<TenantContextMiddleware>();
    })
    .ConfigureServices((context, services) =>
    {
        // Configure camelCase JSON serialization for HTTP responses
        services.Configure<WorkerOptions>(options =>
        {
            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
            };
            jsonOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            options.Serializer = new Azure.Core.Serialization.JsonObjectSerializer(jsonOptions);
        });
        // Instance environment (Development gates real cloud inventory collection)
        services.AddSingleton<IPlatformInfo, PlatformInfo>();

        // Database
        services.AddSingleton<ITenantDbConnectionFactory, TenantDbConnectionFactory>();

        // Repositories
        services.AddScoped<IInventoryRepository, InventoryRepository>();
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<IChangeRepository, ChangeRepository>();
        services.AddScoped<IRecommendationRepository, RecommendationRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IAuditRepository, AuditRepository>();
        services.AddScoped<IAnomalyRepository, AnomalyRepository>();
        services.AddScoped<IIncidentRepository, IncidentRepository>();
        services.AddScoped<IRemediationRepository, RemediationRepository>();
        services.AddScoped<IOrganizationRepository, OrganizationRepository>();
        services.AddScoped<IOrgSsoRepository, OrgSsoRepository>();
        services.AddScoped<ISystemSettingsRepository, SystemSettingsRepository>();
        services.AddScoped<ICloudOrgRepository, CloudOrgRepository>();
        services.AddScoped<ICloudAccountRepository, CloudAccountRepository>();

        // Azure services — Scoped to avoid singleton-depends-on-scoped issue
        services.AddScoped<IAzureCredentialFactory, AzureCredentialFactory>();
        services.AddScoped<IInventoryCollectionService, InventoryCollectionService>();
        services.AddScoped<IAriIngestionService, AriIngestionService>();
        services.AddScoped<IChangePollingService, ChangePollingService>();
        services.AddScoped<IRecommendationSyncService, RecommendationSyncService>();

        // Multi-cloud provider adapters (Azure + AWS + GCP)
        services.AddScoped<ICloudProviderAdapter, AzureProviderAdapter>();
        services.AddScoped<ICloudProviderAdapter, AwsProviderAdapter>();
        services.AddScoped<ICloudProviderAdapter, GcpProviderAdapter>();
        services.AddScoped<ICloudProviderAdapterFactory, CloudProviderAdapterFactory>();
        services.AddScoped<IMultiCloudInventoryService, MultiCloudInventoryService>();

        // AIOps engine: proactive anomaly detection + gated remediation
        services.AddScoped<IAnomalyDetectionService, AnomalyDetectionService>();
        services.AddScoped<IRemediationService, RemediationService>();

        // Local username/password auth — the non-Entra login path
        services.AddScoped<ILocalAuthService, LocalAuthService>();

        // Job queue: Service Bus when configured (default on Azure), otherwise
        // the SQL-table-backed queue — needs no infra beyond the database, so
        // the host never hard-depends on Service Bus just to start.
        var serviceBusConnection = context.Configuration["ServiceBusConnection"]
            ?? context.Configuration.GetConnectionString("ServiceBusConnection");
        if (!string.IsNullOrEmpty(serviceBusConnection))
        {
            services.AddScoped<IJobQueue, AzureServiceBusJobQueue>();
        }
        else
        {
            services.AddScoped<IJobQueue, DatabaseJobQueue>();
        }

        // Secret storage: OpenBao (self-hosted, Vault-API-compatible) — cloud-agnostic,
        // unlike Azure Key Vault. Optional, same as Key Vault was: a deployment with no
        // secret store configured still boots, just can't store/retrieve credentials.
        var openBaoAddress = context.Configuration["OpenBao:Address"];
        if (!string.IsNullOrEmpty(openBaoAddress))
        {
            IAuthMethodInfo authMethod = new TokenAuthMethodInfo(context.Configuration["OpenBao:Token"] ?? "");
            var vaultClientSettings = new VaultClientSettings(openBaoAddress, authMethod);
            services.AddSingleton<IVaultClient>(new VaultClient(vaultClientSettings));
            services.AddSingleton<ISecretStore, OpenBaoSecretStore>();
        }

        // Authentication — two independent login paths, both accepted on every
        // protected endpoint. Entra ID SSO is unchanged; "Local" validates the
        // JWTs LocalAuthService issues from POST /api/auth/login. A deployment
        // with no Entra tenant configured (AzureAd:TenantId empty) simply never
        // receives Entra tokens — the Local scheme still works standalone.
        var config = context.Configuration;
        services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "EntraOrLocal";
                options.DefaultChallengeScheme = "EntraOrLocal";
            })
            .AddJwtBearer("EntraId", options =>
            {
                options.MapInboundClaims = false;
                options.Authority = $"https://login.microsoftonline.com/{config["AzureAd:TenantId"]}/v2.0";
                options.Audience = config["AzureAd:ClientId"];
                options.TokenValidationParameters.ValidIssuer =
                    $"https://login.microsoftonline.com/{config["AzureAd:TenantId"]}/v2.0";
            })
            .AddJwtBearer("Local", options =>
            {
                options.MapInboundClaims = false;
                var signingKey = config["LocalAuth:JwtSigningKey"] ?? "";
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = LocalAuthConstants.Issuer,
                    ValidAudience = LocalAuthConstants.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(LocalAuthConstants.DeriveSigningKey(
                        string.IsNullOrEmpty(signingKey) ? Guid.NewGuid().ToString() : signingKey)),
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                };
            })
            .AddPolicyScheme("EntraOrLocal", "EntraOrLocal", options =>
            {
                // Route each request to whichever scheme actually issued its
                // token, by peeking at the (unvalidated) `iss` claim — the
                // real signature/expiry validation happens in the scheme it's
                // forwarded to, not here.
                options.ForwardDefaultSelector = httpContext =>
                {
                    var header = httpContext.Request.Headers.Authorization.ToString();
                    if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    {
                        var issuer = JwtIssuerReader.TryReadIssuer(header["Bearer ".Length..]);
                        if (issuer == LocalAuthConstants.Issuer) return "Local";
                    }
                    return "EntraId";
                };
            });

        services.AddAuthorization();
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();
    })
    .Build();

host.Run();

/// <summary>
/// Dapper type handler for JSON-serialized columns (tags, identity_principal_ids, etc.)
/// </summary>
sealed class JsonTypeHandler<T> : Dapper.SqlMapper.TypeHandler<T>
{
    public override T? Parse(object value)
    {
        if (value is string json && !string.IsNullOrEmpty(json))
            return System.Text.Json.JsonSerializer.Deserialize<T>(json);
        return default;
    }

    public override void SetValue(System.Data.IDbDataParameter parameter, T? value)
    {
        parameter.Value = value is not null
            ? System.Text.Json.JsonSerializer.Serialize(value)
            : (object)DBNull.Value;
    }
}
