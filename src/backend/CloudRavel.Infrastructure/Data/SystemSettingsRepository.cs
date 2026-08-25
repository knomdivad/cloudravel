using CloudRavel.Core.Interfaces;
using Dapper;
using Microsoft.Extensions.Logging;

namespace CloudRavel.Infrastructure.Data;

/// <summary>
/// Dapper-based global settings store. Admin (RLS-bypassing) connections — this
/// is instance-wide config, not workspace-scoped.
/// </summary>
public sealed class SystemSettingsRepository : ISystemSettingsRepository
{
    private readonly ITenantDbConnectionFactory _connectionFactory;
    private readonly ILogger<SystemSettingsRepository> _logger;

    public SystemSettingsRepository(ITenantDbConnectionFactory connectionFactory, ILogger<SystemSettingsRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<string, string?>> GetAllAsync()
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        var rows = await conn.QueryAsync<(string Key, string? Value)>(
            "SELECT setting_key AS [Key], setting_value AS Value FROM system_settings");
        return rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<string?> GetAsync(string key)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        return await conn.ExecuteScalarAsync<string?>(
            "SELECT setting_value FROM system_settings WHERE setting_key = @Key", new { Key = key });
    }

    public async Task SetAsync(string key, string? value, string updatedBy)
    {
        await using var conn = await _connectionFactory.CreateAdminConnectionAsync();
        const string sql = @"
            MERGE system_settings AS target
            USING (SELECT @Key AS setting_key) AS source
            ON target.setting_key = source.setting_key
            WHEN MATCHED THEN UPDATE SET setting_value = @Value, updated_at = SYSUTCDATETIME(), updated_by = @UpdatedBy
            WHEN NOT MATCHED THEN INSERT (setting_key, setting_value, updated_by)
                VALUES (@Key, @Value, @UpdatedBy);";
        await conn.ExecuteAsync(sql, new { Key = key, Value = value, UpdatedBy = updatedBy });
        _logger.LogInformation("System setting '{Key}' updated by {UpdatedBy}", key, updatedBy);
    }
}
