using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using CloudRavel.Core.Interfaces;

namespace CloudRavel.Infrastructure.Secrets;

/// <summary>
/// ISecretStore backed by Azure Key Vault. Secret names are sanitized to
/// Key Vault's allowed character set (alphanumeric and hyphens).
/// </summary>
public sealed class KeyVaultSecretStore : ISecretStore
{
    private readonly SecretClient _client;

    public KeyVaultSecretStore(Uri vaultUri)
        : this(new SecretClient(vaultUri, new DefaultAzureCredential()))
    {
    }

    public KeyVaultSecretStore(SecretClient client)
    {
        _client = client;
    }

    public async Task<string?> GetSecretAsync(string name)
    {
        try
        {
            var response = await _client.GetSecretAsync(Sanitize(name));
            return response.Value.Value;
        }
        catch (global::Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task SetSecretAsync(string name, string value)
    {
        await _client.SetSecretAsync(Sanitize(name), value);
    }

    /// <summary>
    /// Key Vault secret names: only alphanumeric and hyphens. Paths like
    /// org/{id}/sso-secret become org-{id}-sso-secret.
    /// </summary>
    public static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Secret name is required.", nameof(name));

        var chars = name.Trim().Select(c =>
            char.IsLetterOrDigit(c) ? c :
            c is '/' or '_' or '.' or ' ' ? '-' :
            c == '-' ? '-' : '-').ToArray();
        var sanitized = new string(chars);
        while (sanitized.Contains("--", StringComparison.Ordinal))
            sanitized = sanitized.Replace("--", "-", StringComparison.Ordinal);
        return sanitized.Trim('-');
    }
}
