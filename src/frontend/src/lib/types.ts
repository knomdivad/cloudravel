/**
 * Type definitions for the CloudRavel API responses.
 * These mirror the backend DTOs for type-safe frontend usage.
 */

// --- Admin / RBAC ---

export type SystemRole = 'system_admin' | 'member';
export type OrgRoleName = 'org_admin' | 'cloud_admin' | 'read_only';

export interface Me {
  userId: string;
  displayName: string;
  email: string;
  systemRole: SystemRole;
}

export interface SystemSettings {
  openAiBaseUrl?: string | null;
  openAiModel?: string | null;
  apiKeyConfigured: boolean;
}

export interface AdminUser {
  userId: string;
  displayName: string;
  email: string;
  globalRole: string;
  isActive: boolean;
  authProvider: string;
  username?: string | null;
  lastLoginAt?: string | null;
  /** Only populated in the per-organization member listing. */
  orgRole?: string | null;
}

export interface OrgSso {
  provider: 'none' | 'entra' | 'oidc';
  idpTenantId?: string | null;
  idpClientId?: string | null;
  domain?: string | null;
  enabled: boolean;
  clientSecretConfigured: boolean;
  /** Always 'not_implemented' until per-org IdP federation is enforced. */
  enforcementStatus?: 'not_implemented' | 'enforced';
}

// --- Organization (the in-app workspace above clouds) ---

export interface Organization {
  orgId: string;
  name: string;
  environment: 'Development' | 'Production';
  status: 'active' | 'suspended';
  createdAt: string;
  azureConnected: boolean;
  azureTenantName?: string;
  /** Number of Azure tenant connections (an org can hold more than one). */
  azureOrgCount: number;
  /** The signed-in user's role in this organization. */
  callerRole: 'org_admin' | 'cloud_admin' | 'read_only';
  awsOrgCount: number;
  gcpOrgCount: number;
  cloudCount: number;
}

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
  /** azure | aws | gcp */
  provider?: string;
  /** Azure | AWS | GCP */
  cloud?: string;
  /** subscription | account | project */
  scopeKind?: string;
  /** Azure subscription / AWS account id / GCP project id */
  scopeId?: string;
  /** Friendly linked account/project name when known */
  scopeName?: string;
  /** Parent cloud org (AWS org / GCP org / Azure connection) name */
  cloudOrgName?: string;
  azureTenantId?: string;
  /** Azure RG / AWS service / GCP namespace */
  resourceGroup: string;
  resourceGroupKind?: string;
  subscriptionId: string;
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
  /** Cloud provider (azure / aws / gcp), inferred from resource id when omitted. */
  provider?: string;
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
  /** Cloud provider (azure / aws / gcp), inferred when omitted. */
  provider?: string;
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
  /** Persona: analyst (default) | operations | security | cost */
  mode?: string;
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

// --- AIOps: anomalies, incidents, remediation ---

export type AnomalySeverity = 'Critical' | 'High' | 'Medium' | 'Low' | 'Info';

export interface Anomaly {
  id: number;
  kind:
    | 'ChangeVelocitySpike'
    | 'SecurityPostureRegression'
    | 'CostAnomaly'
    | 'ConfigurationDrift'
    | 'UnusualActorActivity'
    | 'StaleTelemetry'
    | 'ResourceSprawl';
  severity: AnomalySeverity;
  status: 'Open' | 'Acknowledged' | 'Resolved' | 'Suppressed' | 'FalsePositive';
  provider: 'Azure' | 'Aws' | 'Gcp';
  title: string;
  description?: string;
  resourceId?: string;
  metricName?: string;
  observedValue?: number;
  baselineMean?: number;
  score?: number;
  detectedAt: string;
  lastSeenAt: string;
  incidentId?: number;
}

export interface Incident {
  id: number;
  title: string;
  severity: AnomalySeverity;
  status: 'Open' | 'Acknowledged' | 'Mitigated' | 'Resolved' | 'Closed';
  source: string;
  summaryMarkdown?: string;
  assignedTo?: string;
  createdAt: string;
  acknowledgedAt?: string;
  resolvedAt?: string;
  slaDueAt?: string;
  slaBreached: boolean;
  anomalyCount: number;
  remediationCount: number;
  events?: IncidentEvent[];
}

export interface IncidentEvent {
  occurredAt: string;
  eventType: string;
  message: string;
  actorName?: string;
}

export interface RemediationAction {
  id: number;
  playbookKey: string;
  provider: 'Azure' | 'Aws' | 'Gcp';
  resourceId?: string;
  title: string;
  reason: string;
  parametersJson?: string;
  status:
    | 'Proposed'
    | 'PendingApproval'
    | 'Approved'
    | 'Rejected'
    | 'Executing'
    | 'Succeeded'
    | 'Failed'
    | 'Cancelled'
    | 'Expired';
  riskLevel: 'Low' | 'Medium' | 'High';
  requestedBy: string;
  anomalyId?: number;
  incidentId?: number;
  approvalMode: 'auto' | 'gated';
  approvedBy?: string;
  approvedAt?: string;
  rejectedReason?: string;
  executedAt?: string;
  completedAt?: string;
  resultJson?: string;
  errorMessage?: string;
  createdAt: string;
  expiresAt?: string;
}

export interface Playbook {
  playbookKey: string;
  displayName: string;
  description: string;
  provider: 'Azure' | 'Aws' | 'Gcp';
  category: string;
  actionType: string;
  riskLevel: 'Low' | 'Medium' | 'High';
  alwaysRequiresApproval: boolean;
  parametersSchemaJson?: string;
}

export interface CloudAccount {
  accountId: string;
  orgId: string;
  provider: 'Azure' | 'Aws' | 'Gcp';
  externalId: string;
  displayName: string;
  status: 'Connected' | 'Degraded' | 'Disconnected';
  regions?: string[];
  lastInventoryAt?: string;
  lastError?: string;
  resourceCount?: number;
  createdAt: string;
}

export interface CloudOrg {
  orgId: string;
  provider: 'Azure' | 'Aws' | 'Gcp';
  name: string;
  externalId?: string;
  status: 'Active' | 'Degraded' | 'Disconnected' | 'Connected';
  createdAt: string;
  accountCount?: number;
  resourceCount?: number;
  lastInventoryAt?: string;
  accounts: CloudAccount[];
  /** Azure connections only: "all" | "specific". */
  subscriptionScope?: 'all' | 'specific';
  /** Azure connections only: "lighthouse" | "app_registration". */
  onboardingMethod?: string;
  /** True when credentials can be rotated (Azure app reg, or always for accounts). */
  hasCredentials?: boolean;
}

export interface OpsSummary {
  tenantId: string;
  openAnomalies: number;
  criticalAnomalies: number;
  openIncidents: number;
  slaBreachedIncidents: number;
  pendingApprovals: number;
  remediationsLast7d: number;
  autoRemediationsLast7d: number;
  meanTimeToResolveHours?: number;
  autoRemediationMode: string;
  monitoringEnabled: boolean;
  recentAnomalies: Anomaly[];
  recentIncidents: Incident[];
  recentRemediations: RemediationAction[];
  cloudAccounts: CloudAccount[];
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
