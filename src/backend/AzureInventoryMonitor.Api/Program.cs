using AzureInventoryMonitor.Api.Middleware;
using AzureInventoryMonitor.Core.Interfaces;
using AzureInventoryMonitor.Infrastructure.AiOps;
using AzureInventoryMonitor.Infrastructure.Azure;
using AzureInventoryMonitor.Infrastructure.Data;
using AzureInventoryMonitor.Infrastructure.MultiCloud;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using System.Text.Json.Serialization;

// Enable Dapper snake_case → PascalCase column mapping
Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

// Register Dapper type handlers for JSON columns
Dapper.SqlMapper.AddTypeHandler(new JsonTypeHandler<Dictionary<string, string>>());
Dapper.SqlMapper.AddTypeHandler(new JsonTypeHandler<List<string>>());

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication(builder =>
    {
        // Register middleware pipeline
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

        // Key Vault client for storing tenant credentials
        var vaultUri = context.Configuration["KeyVault:VaultUri"]
            ?? context.Configuration["KeyVaultUrl"];
        if (!string.IsNullOrEmpty(vaultUri))
        {
            services.AddSingleton(new SecretClient(new Uri(vaultUri), new DefaultAzureCredential()));
        }

        // Authentication
        services.AddAuthentication()
            .AddJwtBearer("EntraId", options =>
            {
                var config = context.Configuration;
                options.Authority = $"https://login.microsoftonline.com/{config["AzureAd:TenantId"]}/v2.0";
                options.Audience = config["AzureAd:ClientId"];
                options.TokenValidationParameters.ValidIssuer =
                    $"https://login.microsoftonline.com/{config["AzureAd:TenantId"]}/v2.0";
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
