namespace CloudRavel.Core.Interfaces;

/// <summary>
/// Global (non-workspace-scoped) key/value configuration, e.g. OpenAI settings.
/// System-admin only at the app layer. Secret VALUES are never stored here —
/// only a secret NAME pointing at the secret store (mirrors cloud credentials).
/// </summary>
public interface ISystemSettingsRepository
{
    Task<IReadOnlyDictionary<string, string?>> GetAllAsync();
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string? value, string updatedBy);
}

/// <summary>Well-known system_settings keys.</summary>
public static class SystemSettingKeys
{
    public const string OpenAiBaseUrl = "openai.base_url";
    public const string OpenAiModel = "openai.model";
    /// <summary>Name of the secret-store entry holding the OpenAI API key (not the key itself).</summary>
    public const string OpenAiApiKeySecretName = "openai.api_key_secret_name";

    /// <summary>Fixed secret-store path used for the OpenAI API key.</summary>
    public const string OpenAiApiKeySecretPath = "system/openai-apikey";
}
