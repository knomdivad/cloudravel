/**
 * Type definitions for the AIM API responses.
 * These mirror the backend DTOs for type-safe frontend usage.
 */

// --- Tenant ---

export interface TenantSummary {
  tenantId: string;
  displayName: string;
  azureTenantId: string;
  status: 'active' | 'degraded' | 'suspended' | 'offboarded';
  resourceCount?: number;
  lastSnapshotAt?: string;
  openFindings?: number;
  changes24H?: number;
}

// --- Inventory ---

export interface InventoryResponse {
  snapshotId: number;
  snapshotTime: string;
  totalResources: number;
  resources: InventoryResource[];
  pagination: Pagination;
}

export interface InventoryResource {
  resourceId: string;
  subscriptionId: string;
  resourceGroup: string;
  resourceType: string;
  resourceName: string;
  location: string;
  skuName?: string;
  skuTier?: string;
  tags?: Record<string, string>;
  identityType?: string;
  propertiesJson?: string;
  networkingJson?: string;
  securityConfigJson?: string;
}

export interface ResourceTypeSummary {
  resourceType: string;
  count: number;
}

// --- Changes ---

export interface ChangesResponse {
  changes: ResourceChange[];
  pagination: Pagination;
}

export interface ResourceChange {
  changeId: string;
  resourceId: string;
  resourceType: string;
  changeType: 'Create' | 'Update' | 'Delete';
  detectedAt: string;
  changedProperties?: PropertyChange[];
  actorName?: string;
  actorType?: string;
  clientType?: string;
  classification: 'security' | 'governance' | 'cost' | 'operational';
  severity?: 'critical' | 'high' | 'medium' | 'low' | 'info';
}

export interface PropertyChange {
  property: string;
  oldValue?: string;
  newValue?: string;
}

export interface ChangeTimelineBucket {
  bucketStart: string;
  total: number;
  security: number;
  governance: number;
  cost: number;
  operational: number;
}

// --- Recommendations ---

export interface Recommendation {
  source: 'advisor' | 'policy' | 'defender';
  id: string;
  resourceId?: string;
  category: string;
  severity: string;
  title: string;
  description?: string;
  remediationAction?: string;
  estimatedSavings?: number;
  status: string;
  firstSeenAt: string;
  lastSeenAt: string;
}

// --- AI ---

export interface AiQueryRequest {
  query: string;
  conversationId?: string;
}

export interface AiQueryResponse {
  response: string;
  toolsUsed: AiToolInvocation[];
  usage: AiUsage;
}

export interface AiToolInvocation {
  toolName: string;
  arguments: string;
  durationMs: number;
}

export interface AiUsage {
  promptTokens: number;
  completionTokens: number;
  totalTokens: number;
}

// --- Dashboard ---

export interface TenantDashboard {
  tenantId: string;
  tenantName: string;
  totalResources: number;
  lastSnapshotAt?: string;
  changes24H: number;
  openAdvisorRecs: number;
  nonCompliantPolicies: number;
  openDefenderFindings: number;
  estimatedMonthlySavings: number;
  resourceBreakdown: ResourceTypeSummary[];
  changeTimeline: ChangeTimelineBucket[];
  findingsBySeverity: SeverityCount[];
}

export interface SeverityCount {
  severity: string;
  count: number;
}

// --- Common ---

export interface Pagination {
  offset: number;
  limit: number;
  total: number;
  hasMore: boolean;
}

export interface ApiError {
  code: string;
  message: string;
  traceId?: string;
}
