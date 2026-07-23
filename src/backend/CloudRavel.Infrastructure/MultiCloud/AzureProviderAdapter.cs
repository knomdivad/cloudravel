using System.Text;
using System.Text.Json;
using Azure.Core;
using CloudRavel.Core.Interfaces;
using CloudRavel.Core.Models;
using Microsoft.Extensions.Logging;

namespace CloudRavel.Infrastructure.MultiCloud;

/// <summary>
/// Azure adapter. Inventory for Azure flows through the existing ARI/Resource Graph
/// pipeline (IInventoryCollectionService), so this adapter's job is connectivity
/// checks and executing allow-listed remediations via ARM REST.
///
/// Supported action types (nothing else executes):
///   azure.vm.deallocate                  — POST {vmId}/deallocate
///   azure.vm.start                       — POST {vmId}/start
///   azure.storage.disable_public_blob    — PATCH allowBlobPublicAccess=false
///   azure.storage.require_https          — PATCH supportsHttpsTrafficOnly=true
///   azure.resource.apply_tags            — PATCH tags (merge) via Tags API
///   azure.nsg.remove_rule                — DELETE a named NSG security rule
/// </summary>
public sealed class AzureProviderAdapter : ICloudProviderAdapter
{
    private const string ArmBase = "https://management.azure.com";
    private const string ComputeApiVersion = "2024-07-01";
    private const string StorageApiVersion = "2023-05-01";
    private const string NetworkApiVersion = "2024-03-01";
    private const string TagsApiVersion = "2022-09-01";

    private static readonly HttpClient Http = new();

    private readonly IAzureCredentialFactory _credentialFactory;
    private readonly IInventoryCollectionService _inventoryCollection;
    private readonly ILogger<AzureProviderAdapter> _logger;

    public AzureProviderAdapter(
        IAzureCredentialFactory credentialFactory,
        IInventoryCollectionService inventoryCollection,
        ILogger<AzureProviderAdapter> logger)
    {
        _credentialFactory = credentialFactory;
        _inventoryCollection = inventoryCollection;
        _logger = logger;
    }

    public CloudProvider Provider => CloudProvider.Azure;

    public async Task<(bool Healthy, string? Error)> TestConnectivityAsync(CloudAccount account)
    {
        try
        {
            var credential = await _credentialFactory.GetCredentialForTenantAsync(account.TenantId);
            var token = await credential.GetTokenAsync(
                new TokenRequestContext(new[] { "https://management.azure.com/.default" }), default);
            return (!string.IsNullOrEmpty(token.Token), null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<IReadOnlyList<InventoryResource>> CollectInventoryAsync(CloudAccount account, CancellationToken cancellationToken = default)
    {
        // Azure inventory is collected by the first-class Resource Graph pipeline,
        // which persists its own snapshots. Trigger it and return empty here so the
        // multi-cloud worker doesn't double-write Azure rows.
        await _inventoryCollection.CollectInventoryAsync(account.TenantId, "multicloud-worker");
        return [];
    }

    public async Task<RemediationExecutionResult> ExecuteRemediationAsync(
        Guid tenantId, RemediationPlaybook playbook, RemediationAction action, CancellationToken cancellationToken = default)
    {
        var resourceId = action.ResourceId;
        if (string.IsNullOrEmpty(resourceId) && playbook.ActionType != "azure.resource.apply_tags")
            return RemediationExecutionResult.Fail("Azure remediation requires a resource ID.");

        var parameters = ParseParameters(action.ParametersJson);

        try
        {
            var credential = await _credentialFactory.GetCredentialForTenantAsync(tenantId);
            var token = await credential.GetTokenAsync(
                new TokenRequestContext(new[] { "https://management.azure.com/.default" }), cancellationToken);

            return playbook.ActionType switch
            {
                "azure.vm.deallocate" => await ArmPostAsync(token.Token, $"{resourceId}/deallocate", ComputeApiVersion, cancellationToken),
                "azure.vm.start" => await ArmPostAsync(token.Token, $"{resourceId}/start", ComputeApiVersion, cancellationToken),
                "azure.storage.disable_public_blob" => await ArmPatchAsync(token.Token, resourceId!, StorageApiVersion,
                    """{"properties":{"allowBlobPublicAccess":false}}""", cancellationToken),
                "azure.storage.require_https" => await ArmPatchAsync(token.Token, resourceId!, StorageApiVersion,
                    """{"properties":{"supportsHttpsTrafficOnly":true}}""", cancellationToken),
                "azure.resource.apply_tags" => await ApplyTagsAsync(token.Token, resourceId!, parameters, cancellationToken),
                "azure.nsg.remove_rule" => await RemoveNsgRuleAsync(token.Token, resourceId!, parameters, cancellationToken),
                _ => RemediationExecutionResult.Fail($"Action type '{playbook.ActionType}' is not allow-listed for Azure.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Azure remediation {ActionType} failed for {ResourceId}", playbook.ActionType, resourceId);
            return RemediationExecutionResult.Fail(ex.Message);
        }
    }

    private async Task<RemediationExecutionResult> ApplyTagsAsync(
        string token, string resourceId, Dictionary<string, JsonElement> parameters, CancellationToken ct)
    {
        if (!parameters.TryGetValue("tags", out var tagsEl) || tagsEl.ValueKind != JsonValueKind.Object)
            return RemediationExecutionResult.Fail("apply_tags requires a 'tags' object parameter.");

        var body = JsonSerializer.Serialize(new
        {
            operation = "Merge",
            properties = new { tags = JsonSerializer.Deserialize<Dictionary<string, string>>(tagsEl.GetRawText()) }
        });

        var url = $"{ArmBase}{resourceId}/providers/Microsoft.Resources/tags/default?api-version={TagsApiVersion}";
        return await SendAsync(new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        }, token, ct);
    }

    private async Task<RemediationExecutionResult> RemoveNsgRuleAsync(
        string token, string nsgId, Dictionary<string, JsonElement> parameters, CancellationToken ct)
    {
        var ruleName = parameters.TryGetValue("ruleName", out var r) ? r.GetString() : null;
        if (string.IsNullOrEmpty(ruleName))
            return RemediationExecutionResult.Fail("remove_rule requires a 'ruleName' parameter.");

        var url = $"{ArmBase}{nsgId}/securityRules/{Uri.EscapeDataString(ruleName)}?api-version={NetworkApiVersion}";
        return await SendAsync(new HttpRequestMessage(HttpMethod.Delete, url), token, ct);
    }

    private async Task<RemediationExecutionResult> ArmPostAsync(string token, string path, string apiVersion, CancellationToken ct)
    {
        var url = $"{ArmBase}{path}?api-version={apiVersion}";
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent("", Encoding.UTF8, "application/json") };
        return await SendAsync(request, token, ct);
    }

    private async Task<RemediationExecutionResult> ArmPatchAsync(string token, string resourceId, string apiVersion, string body, CancellationToken ct)
    {
        var url = $"{ArmBase}{resourceId}?api-version={apiVersion}";
        var request = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        return await SendAsync(request, token, ct);
    }

    private async Task<RemediationExecutionResult> SendAsync(HttpRequestMessage request, string token, CancellationToken ct)
    {
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var response = await Http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("ARM call {Url} returned {Status}: {Body}", request.RequestUri, response.StatusCode, body);
            return RemediationExecutionResult.Fail($"ARM returned {(int)response.StatusCode}: {Truncate(body, 500)}");
        }

        return RemediationExecutionResult.Ok(JsonSerializer.Serialize(new
        {
            statusCode = (int)response.StatusCode,
            response = Truncate(body, 2000)
        }));
    }

    private static Dictionary<string, JsonElement> ParseParameters(string? parametersJson)
    {
        if (string.IsNullOrEmpty(parametersJson)) return new();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(parametersJson) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
