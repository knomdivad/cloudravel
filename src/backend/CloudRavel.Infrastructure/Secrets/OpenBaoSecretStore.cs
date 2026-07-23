using CloudRavel.Core.Interfaces;
using VaultSharp;
using VaultSharp.Core;

namespace CloudRavel.Infrastructure.Secrets;

/// <summary>
/// ISecretStore backed by OpenBao's KV v2 engine (mounted at "secret/" by default in
/// -dev mode). Each secret's JSON payload is stored under a single "value" key, matching
/// how call sites already treat a secret as one opaque string blob.
/// </summary>
public sealed class OpenBaoSecretStore : ISecretStore
{
    private const string MountPoint = "secret";
    private readonly IVaultClient _client;

    public OpenBaoSecretStore(IVaultClient client)
    {
        _client = client;
    }

    public async Task<string?> GetSecretAsync(string name)
    {
        try
        {
            var secret = await _client.V1.Secrets.KeyValue.V2.ReadSecretAsync(path: name, mountPoint: MountPoint);
            return secret.Data.Data.TryGetValue("value", out var value) ? value?.ToString() : null;
        }
        catch (VaultApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task SetSecretAsync(string name, string value) =>
        _client.V1.Secrets.KeyValue.V2.WriteSecretAsync(
            path: name,
            data: new Dictionary<string, object> { ["value"] = value },
            mountPoint: MountPoint);
}
