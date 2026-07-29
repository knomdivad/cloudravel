using System.Data.Common;
using CloudRavel.Core.Interfaces;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudRavel.Infrastructure.Data;

/// <summary>
/// Creates SQL connections with tenant-scoped Row-Level Security via SESSION_CONTEXT.
///
/// Every connection acquired via CreateConnectionAsync() sets:
///   SESSION_CONTEXT('tenant_id') = {tenantId}
///
/// This ensures that all SQL RLS policies automatically filter data to the requesting tenant,
/// providing defense-in-depth against cross-tenant data leakage.
///
/// Also forces QUOTED_IDENTIFIER / ANSI_NULLS ON so DML against tables with filtered
/// indexes (e.g. users.username) succeeds — required for local login last_login updates.
/// </summary>
public sealed class TenantDbConnectionFactory : ITenantDbConnectionFactory
{
    private readonly string _connectionString;
    private readonly ILogger<TenantDbConnectionFactory> _logger;

    public TenantDbConnectionFactory(IConfiguration configuration, ILogger<TenantDbConnectionFactory> logger)
    {
        _connectionString = configuration.GetConnectionString("CloudRavelDatabase")
            ?? configuration["SqlConnectionString"]
            ?? throw new InvalidOperationException("Connection string 'CloudRavelDatabase' or 'SqlConnectionString' is required.");
        _logger = logger;
    }

    public async Task<DbConnection> CreateConnectionAsync(Guid tenantId)
    {
        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await ApplySessionOptionsAsync(connection);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "EXEC sp_set_session_context @key = N'tenant_id', @value = @tenantId, @read_only = 1";
        cmd.Parameters.Add(new SqlParameter("@tenantId", tenantId));
        await cmd.ExecuteNonQueryAsync();

        _logger.LogDebug("Opened tenant-scoped connection for tenant {TenantId}", tenantId);
        return connection;
    }

    public async Task<DbConnection> CreateAdminConnectionAsync()
    {
        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await ApplySessionOptionsAsync(connection);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "EXEC sp_set_session_context @key = N'bypass_rls', @value = 1, @read_only = 1";
        await cmd.ExecuteNonQueryAsync();

        _logger.LogDebug("Opened admin connection with RLS bypass");
        return connection;
    }

    /// <summary>
    /// Filtered indexes and some indexed views require these SET options.
    /// sqlcmd defaults them OFF; ADO.NET is usually ON, but we set them
    /// explicitly so every connection is consistent.
    /// </summary>
    private static async Task ApplySessionOptionsAsync(SqlConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON;";
        await cmd.ExecuteNonQueryAsync();
    }
}
