using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AzureInventoryMonitor.Core.Interfaces;
using AzureInventoryMonitor.Core.Models;
using Microsoft.Extensions.Logging;

namespace AzureInventoryMonitor.Infrastructure.MultiCloud;

/// <summary>
/// GCP adapter using service-account JWT auth + REST (no Google SDK dependency).
///
/// Credentials come from the secret store (CloudAccount.CredentialSecretName) holding the
/// standard service account key JSON (client_email, private_key, token_uri).
///
/// Inventory: Cloud Asset API — lists every asset in the project in one paginated
/// call (requires cloudasset.googleapis.com enabled and roles/cloudasset.viewer).
///
/// Supported remediation action types:
///   gcp.compute.stop_instance          — params: project?, zone, instance
///   gcp.storage.enforce_pap            — params: bucket (public access prevention)
/// </summary>
public sealed class GcpProviderAdapter : ICloudProviderAdapter
{
    private static readonly HttpClient Http = new();

    private readonly ICloudAccountRepository _accountRepo;
    private readonly ISecretStore? _secretStore;
    private readonly ILogger<GcpProviderAdapter> _logger;

    public GcpProviderAdapter(
        ICloudAccountRepository accountRepo,
        ILogger<GcpProviderAdapter> logger,
        ISecretStore? secretStore = null)
    {
        _accountRepo = accountRepo;
        _secretStore = secretStore;
        _logger = logger;
    }

    public CloudProvider Provider => CloudProvider.Gcp;

    public async Task<(bool Healthy, string? Error)> TestConnectivityAsync(CloudAccount account)
    {
        try
        {
            var token = await GetAccessTokenAsync(account);
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://cloudresourcemanager.googleapis.com/v3/projects/{Uri.EscapeDataString(account.ExternalId)}");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return (false, $"Cloud Resource Manager returned {(int)response.StatusCode}");
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<IReadOnlyList<InventoryResource>> CollectInventoryAsync(CloudAccount account, CancellationToken cancellationToken = default)
    {
        var token = await GetAccessTokenAsync(account);
        var resources = new List<InventoryResource>();
        string? pageToken = null;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            var url = $"https://cloudasset.googleapis.com/v1/projects/{Uri.EscapeDataString(account.ExternalId)}/assets" +
                      $"?contentType=RESOURCE&pageSize=500" +
                      (pageToken != null ? $"&pageToken={Uri.EscapeDataString(pageToken)}" : "");

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var response = await Http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Cloud Asset API returned {(int)response.StatusCode}: {Truncate(body, 500)}");

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var resource = NormalizeAsset(account, asset);
                    if (resource != null) resources.Add(resource);
                }
            }

            pageToken = doc.RootElement.TryGetProperty("nextPageToken", out var npt) ? npt.GetString() : null;
            if (string.IsNullOrEmpty(pageToken)) pageToken = null;
        } while (pageToken != null);

        _logger.LogInformation("Collected {Count} GCP resources for project {Project}", resources.Count, account.ExternalId);
        return resources;
    }

    public async Task<RemediationExecutionResult> ExecuteRemediationAsync(
        Guid tenantId, RemediationPlaybook playbook, RemediationAction action, CancellationToken cancellationToken = default)
    {
        var parameters = ParseParameters(action.ParametersJson);
        var account = await ResolveAccountAsync(tenantId, parameters);
        if (account == null)
            return RemediationExecutionResult.Fail("No connected GCP project linked to this tenant.");

        try
        {
            var token = await GetAccessTokenAsync(account);
            return playbook.ActionType switch
            {
                "gcp.compute.stop_instance" => await StopComputeInstanceAsync(token, account, parameters, cancellationToken),
                "gcp.storage.enforce_pap" => await EnforcePublicAccessPreventionAsync(token, parameters, cancellationToken),
                _ => RemediationExecutionResult.Fail($"Action type '{playbook.ActionType}' is not allow-listed for GCP.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GCP remediation {ActionType} failed", playbook.ActionType);
            return RemediationExecutionResult.Fail(ex.Message);
        }
    }

    private async Task<RemediationExecutionResult> StopComputeInstanceAsync(
        string token, CloudAccount account, Dictionary<string, JsonElement> parameters, CancellationToken ct)
    {
        var project = parameters.TryGetValue("project", out var p) ? p.GetString() ?? account.ExternalId : account.ExternalId;
        var zone = parameters.TryGetValue("zone", out var z) ? z.GetString() : null;
        var instance = parameters.TryGetValue("instance", out var i) ? i.GetString() : null;
        if (string.IsNullOrEmpty(zone) || string.IsNullOrEmpty(instance))
            return RemediationExecutionResult.Fail("stop_instance requires 'zone' and 'instance' parameters.");

        var url = $"https://compute.googleapis.com/compute/v1/projects/{Uri.EscapeDataString(project)}" +
                  $"/zones/{Uri.EscapeDataString(zone)}/instances/{Uri.EscapeDataString(instance)}/stop";
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await Http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            return RemediationExecutionResult.Fail($"Compute stop returned {(int)response.StatusCode}: {Truncate(body, 500)}");

        return RemediationExecutionResult.Ok(JsonSerializer.Serialize(new { project, zone, instance, statusCode = (int)response.StatusCode }));
    }

    private async Task<RemediationExecutionResult> EnforcePublicAccessPreventionAsync(
        string token, Dictionary<string, JsonElement> parameters, CancellationToken ct)
    {
        var bucket = parameters.TryGetValue("bucket", out var b) ? b.GetString() : null;
        if (string.IsNullOrEmpty(bucket))
            return RemediationExecutionResult.Fail("enforce_pap requires a 'bucket' parameter.");

        var url = $"https://storage.googleapis.com/storage/v1/b/{Uri.EscapeDataString(bucket)}";
        var request = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = new StringContent("""{"iamConfiguration":{"publicAccessPrevention":"enforced"}}""",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await Http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            return RemediationExecutionResult.Fail($"Storage PATCH returned {(int)response.StatusCode}: {Truncate(body, 500)}");

        return RemediationExecutionResult.Ok(JsonSerializer.Serialize(new { bucket, statusCode = (int)response.StatusCode }));
    }

    // ---- auth ----

    /// <summary>
    /// Exchanges a self-signed RS256 JWT (service account key) for an OAuth access
    /// token — the standard google service account flow, implemented directly.
    /// </summary>
    private async Task<string> GetAccessTokenAsync(CloudAccount account)
    {
        if (_secretStore == null)
            throw new InvalidOperationException("Secret store is not configured; cannot resolve GCP credentials.");
        if (string.IsNullOrEmpty(account.CredentialSecretName))
            throw new InvalidOperationException($"GCP project {account.ExternalId} has no credential secret configured.");

        var secretValue = await _secretStore.GetSecretAsync(account.CredentialSecretName)
            ?? throw new InvalidOperationException($"No credential secret found for GCP project {account.ExternalId}.");
        using var keyDoc = JsonDocument.Parse(secretValue);
        var root = keyDoc.RootElement;
        var clientEmail = root.GetProperty("client_email").GetString()!;
        var privateKeyPem = root.GetProperty("private_key").GetString()!;
        var tokenUri = root.TryGetProperty("token_uri", out var tu) ? tu.GetString()! : "https://oauth2.googleapis.com/token";

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = clientEmail,
            scope = "https://www.googleapis.com/auth/cloud-platform",
            aud = tokenUri,
            iat = now,
            exp = now + 3600
        }));

        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var signingInput = $"{header}.{claims}";
        var signature = Base64Url(rsa.SignData(Encoding.UTF8.GetBytes(signingInput),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var jwt = $"{signingInput}.{signature}";

        var tokenRequest = new HttpRequestMessage(HttpMethod.Post, tokenUri)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = jwt
            })
        };

        var response = await Http.SendAsync(tokenRequest);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GCP token exchange failed ({(int)response.StatusCode}): {Truncate(body, 300)}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("access_token").GetString()!;
    }

    // ---- helpers ----

    private async Task<CloudAccount?> ResolveAccountAsync(Guid tenantId, Dictionary<string, JsonElement> parameters)
    {
        var accounts = await _accountRepo.GetByTenantAsync(tenantId);
        var gcpAccounts = accounts.Where(a => a.Provider == CloudProvider.Gcp && a.Status != CloudAccountStatus.Disconnected).ToList();

        if (parameters.TryGetValue("accountId", out var acc) && Guid.TryParse(acc.GetString(), out var accountId))
            return gcpAccounts.FirstOrDefault(a => a.AccountId == accountId);
        return gcpAccounts.FirstOrDefault();
    }

    private static InventoryResource? NormalizeAsset(CloudAccount account, JsonElement asset)
    {
        var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
        var assetType = asset.TryGetProperty("assetType", out var at) ? at.GetString() : null;
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(assetType)) return null;

        string location = "global";
        string? propertiesJson = null;
        Dictionary<string, string>? tags = null;

        if (asset.TryGetProperty("resource", out var resource))
        {
            if (resource.TryGetProperty("location", out var loc))
                location = loc.GetString() ?? "global";
            if (resource.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                propertiesJson = data.GetRawText();
                if (data.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Object)
                {
                    tags = new Dictionary<string, string>();
                    foreach (var kv in labels.EnumerateObject())
                        tags[kv.Name] = kv.Value.GetString() ?? "";
                }
            }
        }

        // name format: //compute.googleapis.com/projects/{p}/zones/{z}/instances/{name}
        var displayName = name.Contains('/') ? name[(name.LastIndexOf('/') + 1)..] : name;

        return new InventoryResource
        {
            TenantId = account.TenantId,
            Provider = "gcp",
            ResourceId = name,
            SubscriptionId = account.ExternalId,
            ResourceGroup = assetType.Split('/')[0].Replace(".googleapis.com", ""),
            ResourceType = assetType,
            ResourceName = displayName,
            Location = location,
            Tags = tags is { Count: > 0 } ? tags : null,
            PropertiesJson = propertiesJson
        };
    }

    private static Dictionary<string, JsonElement> ParseParameters(string? parametersJson)
    {
        if (string.IsNullOrEmpty(parametersJson)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(parametersJson) ?? new(); }
        catch { return new(); }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
