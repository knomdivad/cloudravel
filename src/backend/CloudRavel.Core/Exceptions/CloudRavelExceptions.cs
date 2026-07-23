namespace CloudRavel.Core.Exceptions;

/// <summary>
/// Base exception for all CloudRavel domain errors.
/// </summary>
public abstract class CloudRavelException : Exception
{
    public string ErrorCode { get; }

    protected CloudRavelException(string errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }

    protected CloudRavelException(string errorCode, string message, Exception innerException) : base(message, innerException)
    {
        ErrorCode = errorCode;
    }
}

/// <summary>
/// Thrown when a tenant is not found.
/// </summary>
public sealed class TenantNotFoundException : CloudRavelException
{
    public Guid TenantId { get; }

    public TenantNotFoundException(Guid tenantId)
        : base("TENANT_NOT_FOUND", $"Tenant '{tenantId}' not found.")
    {
        TenantId = tenantId;
    }
}

/// <summary>
/// Thrown when a user does not have access to a tenant.
/// </summary>
public sealed class UnauthorizedTenantAccessException : CloudRavelException
{
    public Guid UserId { get; }
    public Guid TenantId { get; }

    public UnauthorizedTenantAccessException(Guid userId, Guid tenantId)
        : base("UNAUTHORIZED_TENANT_ACCESS", $"User '{userId}' does not have access to tenant '{tenantId}'.")
    {
        UserId = userId;
        TenantId = tenantId;
    }
}

/// <summary>
/// Thrown when a tenant is in a state that prevents the requested operation.
/// </summary>
public sealed class TenantInactiveException : CloudRavelException
{
    public Guid TenantId { get; }
    public string Status { get; }

    public TenantInactiveException(Guid tenantId, string status)
        : base("TENANT_INACTIVE", $"Tenant '{tenantId}' is {status} and cannot perform this operation.")
    {
        TenantId = tenantId;
        Status = status;
    }
}

/// <summary>
/// Thrown when a resource is not found in the inventory.
/// </summary>
public sealed class ResourceNotFoundException : CloudRavelException
{
    public string ResourceId { get; }

    public ResourceNotFoundException(string resourceId)
        : base("RESOURCE_NOT_FOUND", $"Resource '{resourceId}' not found in current inventory.")
    {
        ResourceId = resourceId;
    }
}

/// <summary>
/// Thrown when snapshot ingestion fails.
/// </summary>
public sealed class IngestionException : CloudRavelException
{
    public Guid TenantId { get; }
    public long? SnapshotId { get; }

    public IngestionException(Guid tenantId, long? snapshotId, string message, Exception? inner = null)
        : base("INGESTION_FAILED", message, inner ?? new Exception(message))
    {
        TenantId = tenantId;
        SnapshotId = snapshotId;
    }
}

/// <summary>
/// Thrown when request validation fails.
/// </summary>
public sealed class ValidationException : CloudRavelException
{
    public IDictionary<string, string[]> Errors { get; }

    public ValidationException(string message) : base("VALIDATION_FAILED", message)
    {
        Errors = new Dictionary<string, string[]>();
    }

    public ValidationException(IDictionary<string, string[]> errors)
        : base("VALIDATION_FAILED", "One or more validation errors occurred.")
    {
        Errors = errors;
    }
}

/// <summary>
/// Thrown when Azure credential resolution fails for a tenant.
/// </summary>
public sealed class CredentialResolutionException : CloudRavelException
{
    public Guid TenantId { get; }

    public CredentialResolutionException(Guid tenantId, string message, Exception? inner = null)
        : base("CREDENTIAL_FAILED", message, inner ?? new Exception(message))
    {
        TenantId = tenantId;
    }
}
