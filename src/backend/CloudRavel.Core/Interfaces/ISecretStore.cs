namespace CloudRavel.Core.Interfaces;

/// <summary>
/// Cloud-agnostic secret storage — backed by OpenBao (self-hosted, Vault-API-compatible)
/// so credential storage doesn't hard-depend on any one cloud's secret manager.
/// </summary>
public interface ISecretStore
{
    Task<string?> GetSecretAsync(string name);
    Task SetSecretAsync(string name, string value);
    /// <summary>
    /// Best-effort delete. Missing secrets are not an error (idempotent cleanup).
    /// </summary>
    Task DeleteSecretAsync(string name);
}
