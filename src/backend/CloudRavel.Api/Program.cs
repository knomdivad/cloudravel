using CloudRavel.Api.Auth;
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

        var config = context.Configuration;

        // CORS allow-list (comma-separated). Defaults to local Next.js origin.
        HttpResponseDataExtensions.ConfigureFromRaw(config["Cors:AllowedOrigins"]);

        // Instance environment (Development gates real cloud inventory collection)
        services.AddSingleton<IPlatformInfo, PlatformInfo>();
        services.AddSingleton<LoginRateLimiter>();

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
        services.AddScoped<IMultiCloudPostureService, MultiCloudPostureService>();

        // AIOps engine: proactive anomaly detection + gated remediation
        services.AddScoped<IAnomalyDetectionService, AnomalyDetectionService>();
        services.AddScoped<IRemediationService, RemediationService>();

        // Local username/password auth — the non-Entra login path
        services.AddScoped<ILocalAuthService, LocalAuthService>();

        // Job queue: Service Bus when configured (default on Azure), otherwise
        // the SQL-table-backed queue — needs no infra beyond the database, so
        // the host never hard-depends on Service Bus just to start.
        var serviceBusConnection = config["ServiceBusConnection"]
            ?? config.GetConnectionString("ServiceBusConnection");
        if (!string.IsNullOrEmpty(serviceBusConnection))
        {
            services.AddScoped<IJobQueue, AzureServiceBusJobQueue>();
        }
        else
        {
            services.AddScoped<IJobQueue, DatabaseJobQueue>();
        }

        // Secret storage: OpenBao (default for self-host) or Azure Key Vault.
        // SecretStore:Provider = OpenBao | KeyVault (case-insensitive).
        // A deployment with no store configured still boots, but credentialed
        // cloud links and AI key storage fail closed at the API layer.
        var secretProvider = (config["SecretStore:Provider"] ?? "").Trim();
        var openBaoAddress = config["OpenBao:Address"];
        var keyVaultUri = config["KeyVault:VaultUri"] ?? config["KeyVault__VaultUri"];

        if (string.Equals(secretProvider, "KeyVault", StringComparison.OrdinalIgnoreCase)
            || (string.IsNullOrEmpty(openBaoAddress) && !string.IsNullOrEmpty(keyVaultUri)
                && !string.Equals(secretProvider, "OpenBao", StringComparison.OrdinalIgnoreCase)))
        {
            if (string.IsNullOrEmpty(keyVaultUri))
                throw new InvalidOperationException("SecretStore:Provider=KeyVault requires KeyVault:VaultUri.");
            services.AddSingleton<ISecretStore>(new KeyVaultSecretStore(new Uri(keyVaultUri)));
        }
        else if (!string.IsNullOrEmpty(openBaoAddress)
                 || string.Equals(secretProvider, "OpenBao", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(openBaoAddress))
                throw new InvalidOperationException("SecretStore:Provider=OpenBao requires OpenBao:Address.");
            // Token may come from env (OpenBao:Token) or a file written by the
            // persistent OpenBao entrypoint (OpenBao:TokenFile) so restarts keep working.
            var openBaoToken = config["OpenBao:Token"];
            var tokenFile = config["OpenBao:TokenFile"];
            if (string.IsNullOrWhiteSpace(openBaoToken) && !string.IsNullOrWhiteSpace(tokenFile)
                && File.Exists(tokenFile))
            {
                openBaoToken = File.ReadAllText(tokenFile).Trim();
            }
            if (string.IsNullOrWhiteSpace(openBaoToken))
                throw new InvalidOperationException(
                    "OpenBao requires OpenBao:Token or a readable OpenBao:TokenFile (e.g. /openbao-data/.cloudravel-root-token).");
            IAuthMethodInfo authMethod = new TokenAuthMethodInfo(openBaoToken);
            var vaultClientSettings = new VaultClientSettings(openBaoAddress, authMethod);
            services.AddSingleton<IVaultClient>(new VaultClient(vaultClientSettings));
            services.AddSingleton<ISecretStore, OpenBaoSecretStore>();
        }

        // Authentication — two independent login paths, both accepted on every
        // protected endpoint. LocalAuth:JwtSigningKey is required (fail closed).
        var signingKey = config["LocalAuth:JwtSigningKey"];
        if (string.IsNullOrWhiteSpace(signingKey))
            throw new InvalidOperationException(
                "LocalAuth:JwtSigningKey is required. Set LocalAuth__JwtSigningKey (or LOCAL_AUTH_JWT_SIGNING_KEY in compose) to a long random value.");

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
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = LocalAuthConstants.Issuer,
                    ValidAudience = LocalAuthConstants.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(LocalAuthConstants.DeriveSigningKey(signingKey)),
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
