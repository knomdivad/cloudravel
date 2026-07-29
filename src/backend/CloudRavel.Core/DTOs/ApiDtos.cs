using System.Text.Json.Serialization;

namespace CloudRavel.Core.DTOs;

// ============================================================================
// API Request/Response DTOs
// ============================================================================

// --- Tenant ---

public sealed class TenantListResponse
{
    public IReadOnlyList<TenantSummaryDto> Tenants { get; set; } = [];
}

public sealed class TenantSummaryDto
{
    public Guid TenantId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string AzureTenantId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int? ResourceCount { get; set; }
    public DateTime? LastSnapshotAt { get; set; }
    public int? OpenFindings { get; set; }
    public int? Changes24H { get; set; }
}

public sealed class OnboardTenantRequest
{
    public string DisplayName { get; set; } = string.Empty;
    public string AzureTenantId { get; set; } = string.Empty;
    public string OnboardingMethod { get; set; } = string.Empty; // lighthouse | app_registration
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? CertificateThumbprint { get; set; }
    public string? LighthouseDelegationId { get; set; }
    public List<string>? SubscriptionIds { get; set; }
}

// --- Organizations (the in-app workspace above clouds) ---

public sealed class OrganizationListResponse
{
    public IReadOnlyList<OrganizationDto> Organizations { get; set; } = [];
}

public sealed class OrganizationDto
{
    public Guid OrgId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Environment { get; set; } = "Development";
    public string Status { get; set; } = "active";
    public DateTime CreatedAt { get; set; }

    // Cloud rollup for the selector / org card
    public bool AzureConnected { get; set; }
    public string? AzureTenantName { get; set; }
    /// <summary>Number of Azure tenant connections (an org can hold more than one).</summary>
    public int AzureOrgCount { get; set; }
    public int AwsOrgCount { get; set; }
    public int GcpOrgCount { get; set; }
    public int CloudCount { get; set; }
    /// <summary>The requesting user's role in this organization (org_admin | cloud_admin | read_only).</summary>
    public string CallerRole { get; set; } = "read_only";
}

public sealed class CreateOrganizationRequest
{
    public string Name { get; set; } = string.Empty;
    /// <summary>Development (default) | Production.</summary>
    public string? Environment { get; set; }
}

/// <summary>
/// Connect (or, for a fresh org, establish) the Azure tenant for an Organization.
/// Materialises the tenants row at tenant_id = org_id — it never creates a new org.
/// </summary>
public sealed class ConnectAzureTenantRequest
{
    public string DisplayName { get; set; } = string.Empty;
    public string AzureTenantId { get; set; } = string.Empty;
    public string OnboardingMethod { get; set; } = "lighthouse"; // lighthouse | app_registration
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? LighthouseDelegationId { get; set; }
    /// <summary>Specific subscriptions to monitor. Empty/null = all subscriptions.</summary>
    public List<string>? SubscriptionIds { get; set; }
}

// --- Inventory ---

public sealed class InventoryResponse
{
    public long SnapshotId { get; set; }
    public DateTime SnapshotTime { get; set; }
    public int TotalResources { get; set; }
    public IReadOnlyList<InventoryResourceDto> Resources { get; set; } = [];
    public PaginationDto Pagination { get; set; } = new();
}

public sealed class InventoryResourceDto
{
    public string ResourceId { get; set; } = string.Empty;
    /// <summary>azure | aws | gcp (normalized lower-case from inventory_resources.provider).</summary>
    public string Provider { get; set; } = "azure";
    /// <summary>Display label: Azure | AWS | GCP.</summary>
    public string Cloud { get; set; } = "Azure";
    /// <summary>subscription | account | project — meaning of <see cref="ScopeId"/>.</summary>
    public string ScopeKind { get; set; } = "subscription";
    /// <summary>Azure subscription GUID, AWS account id, or GCP project id.</summary>
    public string ScopeId { get; set; } = string.Empty;
    /// <summary>Friendly name when known (linked cloud_accounts.display_name).</summary>
    public string? ScopeName { get; set; }
    /// <summary>Parent cloud org (AWS org / GCP org / Azure connection) display name when known.</summary>
    public string? CloudOrgName { get; set; }
    /// <summary>Azure AD tenant id (external_id) when this row is under an Azure connection.</summary>
    public string? AzureTenantId { get; set; }
    /// <summary>Azure resource group, AWS service, or GCP asset type prefix — raw DB value.</summary>
    public string ResourceGroup { get; set; } = string.Empty;
    /// <summary>UI label for resource group column: Resource group | Service | Namespace.</summary>
    public string ResourceGroupKind { get; set; } = "Resource group";
    public string SubscriptionId { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public string ResourceName { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string? SkuName { get; set; }
    public string? SkuTier { get; set; }
    public Dictionary<string, string>? Tags { get; set; }
    public string? IdentityType { get; set; }
}

public sealed class InventoryDiffResponse
{
    public long FromSnapshotId { get; set; }
    public long ToSnapshotId { get; set; }
    public DateTime FromTime { get; set; }
    public DateTime ToTime { get; set; }
    public IReadOnlyList<ResourceDiffDto> Added { get; set; } = [];
    public IReadOnlyList<ResourceDiffDto> Removed { get; set; } = [];
    public IReadOnlyList<ResourcePropertyDiffDto> Modified { get; set; } = [];
}

public sealed class ResourceDiffDto
{
    public string ResourceId { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public string ResourceName { get; set; } = string.Empty;
}

public sealed class ResourcePropertyDiffDto
{
    public string ResourceId { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public IReadOnlyList<PropertyChangeDto> Changes { get; set; } = [];
}

public sealed class PropertyChangeDto
{
    public string Property { get; set; } = string.Empty;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
}

// --- Changes ---

public sealed class ChangesResponse
{
    public IReadOnlyList<ResourceChangeDto> Changes { get; set; } = [];
    public PaginationDto Pagination { get; set; } = new();
}

public sealed class ResourceChangeDto
{
    public string ChangeId { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    /// <summary>Inferred cloud provider (azure / aws / gcp) for UI badges.</summary>
    public string Provider { get; set; } = "azure";
    public string ChangeType { get; set; } = string.Empty;
    public DateTime DetectedAt { get; set; }
    public IReadOnlyList<PropertyChangeDto>? ChangedProperties { get; set; }
    public string? ActorName { get; set; }
    public string? ActorType { get; set; }
    public string? ClientType { get; set; }
    public string Classification { get; set; } = string.Empty;
    public string? Severity { get; set; }
}

// --- Recommendations ---

public sealed class RecommendationsResponse
{
    public IReadOnlyList<RecommendationDto> Recommendations { get; set; } = [];
    public PaginationDto Pagination { get; set; } = new();
}

public sealed class RecommendationDto
{
    public string Source { get; set; } = string.Empty; // advisor, policy, defender
    public string Id { get; set; } = string.Empty;
    public string? ResourceId { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? RemediationAction { get; set; }
    public decimal? EstimatedSavings { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
}

// --- AI ---

public sealed class AiQueryRequest
{
    public string Query { get; set; } = string.Empty;
    public string? ConversationId { get; set; }

    /// <summary>
    /// Persona: analyst (default) | operations | security | cost.
    /// 'operations' unlocks the AIOps triage workflow including remediation proposals.
    /// </summary>
    public string? Mode { get; set; }
}

public sealed class AiQueryResponse
{
    public string Response { get; set; } = string.Empty;
    public IReadOnlyList<AiToolInvocationDto> ToolsUsed { get; set; } = [];
    public AiUsageDto Usage { get; set; } = new();
}

public sealed class AiToolInvocationDto
{
    public string ToolName { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public int DurationMs { get; set; }
}

public sealed class AiUsageDto
{
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }
}

// --- Dashboard ---

public sealed class TenantDashboardResponse
{
    public Guid TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public int TotalResources { get; set; }
    public DateTime? LastSnapshotAt { get; set; }
    public int Changes24H { get; set; }
    public int OpenAdvisorRecs { get; set; }
    public int NonCompliantPolicies { get; set; }
    public int OpenDefenderFindings { get; set; }
    public decimal EstimatedMonthlySavings { get; set; }
    public IReadOnlyList<ResourceTypeSummaryDto> ResourceBreakdown { get; set; } = [];
    public IReadOnlyList<ChangeTimelineDto> ChangeTimeline { get; set; } = [];
    public IReadOnlyList<SeverityCountDto> FindingsBySeverity { get; set; } = [];
}

public sealed class ResourceTypeSummaryDto
{
    public string ResourceType { get; set; } = string.Empty;
    public int Count { get; set; }
}

public sealed class ChangeTimelineDto
{
    public DateTime BucketStart { get; set; }
    public int Total { get; set; }
    public int Security { get; set; }
    public int Governance { get; set; }
    public int Cost { get; set; }
    public int Operational { get; set; }
}

public sealed class SeverityCountDto
{
    public string Severity { get; set; } = string.Empty;
    public int Count { get; set; }
}

// --- Common ---

public sealed class PaginationDto
{
    public int Offset { get; set; }
    public int Limit { get; set; }
    public int Total { get; set; }
    public bool HasMore => Offset + Limit < Total;
}

public sealed class ErrorResponse
{
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? TraceId { get; set; }
}

// --- Local auth ---

public sealed class LoginRequestDto
{
    /// <summary>Login identity (email). Preferred field for the UI.</summary>
    public string? Username { get; set; }
    /// <summary>Alias for Username — browsers label the field "Email".</summary>
    public string? Email { get; set; }
    public string Password { get; set; } = string.Empty;

    /// <summary>Resolved login identity: username, else email.</summary>
    public string LoginIdentity =>
        !string.IsNullOrWhiteSpace(Username) ? Username.Trim()
        : !string.IsNullOrWhiteSpace(Email) ? Email.Trim()
        : string.Empty;
}

public sealed class LoginResponseDto
{
    public string Token { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public AuthUserDto User { get; set; } = new();
}

public sealed class AuthUserDto
{
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string GlobalRole { get; set; } = string.Empty;
}
