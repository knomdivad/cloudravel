import { InteractionRequiredAuthError } from '@azure/msal-browser';
import { apiScopes } from './auth';
import { msalInstance } from './msalInstance';
import { getStoredLocalToken } from './localAuth';
import type {
  TenantSummary,
  TenantDashboard,
  InventoryResponse,
  InventoryResource,
  ResourceTypeSummary,
  ChangesResponse,
  ChangeTimelineBucket,
  Recommendation,
  AiQueryRequest,
  AiQueryResponse,
  ApiError,
  Anomaly,
  Incident,
  RemediationAction,
  Playbook,
  CloudAccount,
  CloudOrg,
  Organization,
  OpsSummary,
  Me,
  SystemSettings,
  AdminUser,
  OrgSso,
} from './types';

const API_BASE = process.env.NEXT_PUBLIC_API_BASE_URL || 'http://localhost:7071/api';

/**
 * Acquires a fresh access token for API calls.
 * Checks for a local (username/password) session first; falls back to MSAL
 * (silent acquisition, then interactive) for Entra ID SSO sessions.
 */
async function getAccessToken(): Promise<string> {
  const local = getStoredLocalToken();
  if (local) {
    return local.token;
  }

  const accounts = msalInstance.getAllAccounts();

  if (accounts.length === 0) {
    throw new Error('No authenticated user. Please sign in.');
  }

  try {
    const response = await msalInstance.acquireTokenSilent({
      scopes: apiScopes.default,
      account: accounts[0],
    });
    return response.accessToken;
  } catch (error) {
    if (error instanceof InteractionRequiredAuthError) {
      const response = await msalInstance.acquireTokenPopup({
        scopes: apiScopes.default,
      });
      return response.accessToken;
    }
    throw error;
  }
}

/**
 * Makes an authenticated API call with tenant context.
 * Every request includes:
 *   - Authorization header (JWT from Entra ID)
 *   - X-Tenant-Id header (current tenant selection)
 */
async function apiCall<T>(
  path: string,
  tenantId: string,
  options: RequestInit = {}
): Promise<T> {
  const token = await getAccessToken();

  const response = await fetch(`${API_BASE}${path}`, {
    ...options,
    headers: {
      'Content-Type': 'application/json',
      Authorization: `Bearer ${token}`,
      'X-Tenant-Id': tenantId,
      ...options.headers,
    },
  });

  if (!response.ok) {
    const raw = await response.text();
    let message = `API error: ${response.status} ${response.statusText}`;
    try {
      const error = JSON.parse(raw) as ApiError & { Message?: string; code?: string; Code?: string };
      message = error.message || error.Message || message;
      if (error.code || error.Code) message = `[${error.code || error.Code}] ${message}`;
    } catch {
      if (raw?.trim()) message = `${message} — ${raw.trim().slice(0, 200)}`;
    }
    throw new Error(message);
  }

  const text = await response.text();
  if (!text) return undefined as T;
  return JSON.parse(text) as T;
}

// A sentinel used for workspace-registry endpoints (list/create) that aren't
// scoped to any one workspace. A global admin passes the access check for it.
const NO_WORKSPACE = '00000000-0000-0000-0000-000000000000';

// ============================================================================
// Organization API — the in-app workspace above clouds
// ============================================================================

export async function getOrganizations(): Promise<Organization[]> {
  const token = await getAccessToken();
  const response = await fetch(`${API_BASE}/organizations`, {
    headers: {
      Authorization: `Bearer ${token}`,
      'X-Tenant-Id': NO_WORKSPACE,
    },
  });
  const data = await response.json();
  return data.organizations ?? data.Organizations ?? [];
}

export async function createOrganization(request: {
  name: string;
  environment?: string;
}): Promise<Organization> {
  return apiCall<Organization>('/organizations', NO_WORKSPACE, {
    method: 'POST',
    body: JSON.stringify(request),
  });
}

/** Connect the Azure tenant for an organization (never creates a new org). */
export async function connectAzureTenant(
  orgId: string,
  request: {
    displayName: string;
    azureTenantId: string;
    onboardingMethod: string;
    clientId?: string;
    clientSecret?: string;
    lighthouseDelegationId?: string;
    subscriptionIds?: string[];
  }
): Promise<Organization> {
  return apiCall<Organization>(`/organizations/${orgId}/azure`, orgId, {
    method: 'POST',
    body: JSON.stringify(request),
  });
}

// ============================================================================
// Auth / identity
// ============================================================================

/** The signed-in user's identity + system role — works for local and Entra sessions. */
export async function getMe(): Promise<Me> {
  return apiCall<Me>('/auth/me', NO_WORKSPACE);
}

// ============================================================================
// System admin API (system_admin only)
// ============================================================================

export async function getSystemSettings(): Promise<SystemSettings> {
  return apiCall<SystemSettings>('/admin/settings', NO_WORKSPACE);
}

export async function updateSystemSettings(request: {
  openAiBaseUrl?: string;
  openAiModel?: string;
  openAiApiKey?: string;
}): Promise<SystemSettings> {
  return apiCall<SystemSettings>('/admin/settings', NO_WORKSPACE, {
    method: 'PUT',
    body: JSON.stringify(request),
  });
}

function normalizeAdminUser(raw: Record<string, unknown>): AdminUser {
  return {
    userId: String(raw.userId ?? raw.UserId ?? ''),
    displayName: String(raw.displayName ?? raw.DisplayName ?? ''),
    email: String(raw.email ?? raw.Email ?? ''),
    globalRole: String(raw.globalRole ?? raw.GlobalRole ?? 'member'),
    isActive: Boolean(raw.isActive ?? raw.IsActive ?? true),
    authProvider: String(raw.authProvider ?? raw.AuthProvider ?? 'local'),
    username: (raw.username ?? raw.Username ?? null) as string | null,
    lastLoginAt: (raw.lastLoginAt ?? raw.LastLoginAt ?? null) as string | null,
    orgRole: (raw.orgRole ?? raw.OrgRole ?? null) as string | null,
  };
}

export async function getAllUsers(): Promise<AdminUser[]> {
  // Prefer dedicated list route; fall back to multi-method admin/users.
  let res: { users?: unknown[]; Users?: unknown[] };
  try {
    res = await apiCall('/system/users', NO_WORKSPACE);
  } catch {
    res = await apiCall('/admin/users', NO_WORKSPACE);
  }
  const list = res.users ?? res.Users ?? [];
  return list.map((u) => normalizeAdminUser(u as Record<string, unknown>));
}

export async function createUser(request: {
  displayName: string;
  email: string;
  username?: string;
  password: string;
  globalRole?: string;
}): Promise<AdminUser> {
  const email = request.email.trim().toLowerCase();
  // Prefer unambiguous route (also available as POST /admin/users and /system/users).
  const raw = await apiCall<Record<string, unknown>>('/system/users', NO_WORKSPACE, {
    method: 'POST',
    body: JSON.stringify({
      displayName: request.displayName,
      email,
      username: email,
      password: request.password,
      globalRole: request.globalRole,
    }),
  });
  return normalizeAdminUser(raw);
}

export async function updateUser(
  userId: string,
  request: { globalRole?: string; isActive?: boolean; password?: string }
): Promise<AdminUser> {
  return apiCall<AdminUser>(`/admin/users/${userId}`, NO_WORKSPACE, {
    method: 'PATCH',
    body: JSON.stringify(request),
  });
}

// ============================================================================
// Organization admin API (org_admin only) — users + SSO
// ============================================================================

export async function getOrgUsers(orgId: string): Promise<AdminUser[]> {
  const res = await apiCall<{ users?: unknown[]; Users?: unknown[] }>(`/organizations/${orgId}/users`, orgId);
  const list = res.users ?? res.Users ?? [];
  return list.map((u) => normalizeAdminUser(u as Record<string, unknown>));
}

export async function addOrgUser(
  orgId: string,
  request: { username?: string; email?: string; displayName?: string; password?: string; role: string }
): Promise<AdminUser> {
  return apiCall<AdminUser>(`/organizations/${orgId}/users`, orgId, {
    method: 'POST',
    body: JSON.stringify(request),
  });
}

export async function updateOrgUserRole(orgId: string, userId: string, role: string): Promise<void> {
  await apiCall(`/organizations/${orgId}/users/${userId}`, orgId, {
    method: 'PATCH',
    body: JSON.stringify({ role }),
  });
}

export async function removeOrgUser(orgId: string, userId: string): Promise<void> {
  await apiCall(`/organizations/${orgId}/users/${userId}`, orgId, { method: 'DELETE' });
}

export async function getOrgSso(orgId: string): Promise<OrgSso> {
  return apiCall<OrgSso>(`/organizations/${orgId}/sso`, orgId);
}

export async function updateOrgSso(
  orgId: string,
  request: {
    provider: string;
    idpTenantId?: string;
    idpClientId?: string;
    domain?: string;
    enabled: boolean;
    clientSecret?: string;
  }
): Promise<OrgSso> {
  return apiCall<OrgSso>(`/organizations/${orgId}/sso`, orgId, {
    method: 'PUT',
    body: JSON.stringify(request),
  });
}

// ============================================================================
// Tenant API
// ============================================================================

export async function getTenants(): Promise<TenantSummary[]> {
  const token = await getAccessToken();
  const response = await fetch(`${API_BASE}/tenants`, {
    headers: {
      Authorization: `Bearer ${token}`,
      'X-Tenant-Id': '00000000-0000-0000-0000-000000000000',
    },
  });
  const data = await response.json();
  return data.tenants ?? data.Tenants;
}

// ============================================================================
// Inventory API
// ============================================================================

export async function getInventoryResources(
  tenantId: string,
  params?: {
    resourceType?: string;
    subscriptionId?: string;
    resourceGroup?: string;
    offset?: number;
    limit?: number;
  }
): Promise<InventoryResponse> {
  const searchParams = new URLSearchParams();
  if (params?.resourceType) searchParams.set('resourceType', params.resourceType);
  if (params?.subscriptionId) searchParams.set('subscriptionId', params.subscriptionId);
  if (params?.resourceGroup) searchParams.set('resourceGroup', params.resourceGroup);
  if (params?.offset) searchParams.set('offset', params.offset.toString());
  if (params?.limit) searchParams.set('limit', params.limit.toString());

  const query = searchParams.toString();
  return apiCall<InventoryResponse>(`/inventory/resources${query ? `?${query}` : ''}`, tenantId);
}

export async function getResourceDetail(
  tenantId: string,
  resourceId: string
): Promise<InventoryResource> {
  return apiCall<InventoryResource>(`/inventory/resource/${resourceId}`, tenantId);
}

export async function getInventorySummary(
  tenantId: string
): Promise<{ totalResources: number; resourceTypes: ResourceTypeSummary[] }> {
  return apiCall(`/inventory/summary`, tenantId);
}

export async function triggerSnapshot(tenantId: string): Promise<{ snapshotId: number }> {
  return apiCall('/inventory/snapshots/trigger', tenantId, { method: 'POST' });
}

// ============================================================================
// Changes API
// ============================================================================

export async function getChanges(
  tenantId: string,
  params?: {
    from?: string;
    to?: string;
    resourceId?: string;
    classification?: string;
    offset?: number;
    limit?: number;
  }
): Promise<ChangesResponse> {
  const searchParams = new URLSearchParams();
  if (params?.from) searchParams.set('from', params.from);
  if (params?.to) searchParams.set('to', params.to);
  if (params?.resourceId) searchParams.set('resourceId', params.resourceId);
  if (params?.classification) searchParams.set('classification', params.classification);
  if (params?.offset) searchParams.set('offset', params.offset.toString());
  if (params?.limit) searchParams.set('limit', params.limit.toString());

  const query = searchParams.toString();
  return apiCall<ChangesResponse>(`/changes${query ? `?${query}` : ''}`, tenantId);
}

export async function getChangeTimeline(
  tenantId: string,
  from: string,
  to: string
): Promise<{ buckets: ChangeTimelineBucket[] }> {
  return apiCall(`/changes/timeline?from=${from}&to=${to}`, tenantId);
}

// ============================================================================
// Recommendations API
// ============================================================================

export async function getAdvisorRecommendations(
  tenantId: string,
  params?: { category?: string; status?: string; limit?: number }
): Promise<{ recommendations: Recommendation[] }> {
  const searchParams = new URLSearchParams();
  if (params?.category) searchParams.set('category', params.category);
  if (params?.status) searchParams.set('status', params.status);
  if (params?.limit) searchParams.set('limit', params.limit.toString());

  const query = searchParams.toString();
  return apiCall(`/recommendations/advisor${query ? `?${query}` : ''}`, tenantId);
}

export async function getDefenderFindings(
  tenantId: string,
  params?: { severity?: string; status?: string; limit?: number }
): Promise<{ findings: Recommendation[] }> {
  const searchParams = new URLSearchParams();
  if (params?.severity) searchParams.set('severity', params.severity);
  if (params?.status) searchParams.set('status', params.status);
  if (params?.limit) searchParams.set('limit', params.limit.toString());

  const query = searchParams.toString();
  return apiCall(`/recommendations/defender${query ? `?${query}` : ''}`, tenantId);
}

export async function getPolicyCompliance(
  tenantId: string,
  params?: { state?: string; limit?: number }
): Promise<{ records: any[] }> {
  const searchParams = new URLSearchParams();
  if (params?.state) searchParams.set('state', params.state);
  if (params?.limit) searchParams.set('limit', params.limit.toString());

  const query = searchParams.toString();
  return apiCall(`/recommendations/policy${query ? `?${query}` : ''}`, tenantId);
}

// ============================================================================
// AI API
// ============================================================================

export async function queryAi(tenantId: string, request: AiQueryRequest): Promise<AiQueryResponse> {
  return apiCall<AiQueryResponse>('/ai/query', tenantId, {
    method: 'POST',
    body: JSON.stringify(request),
  });
}

// ============================================================================
// Tenant Management API
// ============================================================================

export async function onboardTenant(request: {
  displayName: string;
  azureTenantId: string;
  onboardingMethod: string;
  clientId?: string;
  clientSecret?: string;
  lighthouseDelegationId?: string;
  subscriptionIds?: string[];
}): Promise<{ tenantId: string }> {
  const token = await getAccessToken();
  const response = await fetch(`${API_BASE}/tenants`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      Authorization: `Bearer ${token}`,
      'X-Tenant-Id': '00000000-0000-0000-0000-000000000000',
    },
    body: JSON.stringify(request),
  });
  if (!response.ok) {
    const error = await response.json().catch(() => ({ message: `HTTP ${response.status}: ${response.statusText}` }));
    throw new Error(error.message || `HTTP ${response.status}: ${response.statusText}`);
  }
  return response.json();
}

export async function updateTenantStatus(
  tenantId: string,
  status: string
): Promise<void> {
  const token = await getAccessToken();
  const response = await fetch(`${API_BASE}/tenants/${tenantId}/status`, {
    method: 'PATCH',
    headers: {
      'Content-Type': 'application/json',
      Authorization: `Bearer ${token}`,
      'X-Tenant-Id': tenantId,
    },
    body: JSON.stringify({ status }),
  });
  if (!response.ok) {
    const error = await response.json().catch(() => ({ message: 'Failed to update tenant status' }));
    throw new Error(error.message);
  }
}

// ============================================================================
// Dashboard API
// ============================================================================

export async function getDashboard(
  tenantId: string
): Promise<TenantDashboard> {
  return apiCall<TenantDashboard>('/dashboard', tenantId);
}

// ============================================================================
// AIOps API: operations summary, anomalies, incidents, remediation, cloud accounts
// ============================================================================

export async function getOpsSummary(tenantId: string): Promise<OpsSummary> {
  return apiCall<OpsSummary>('/operations/summary', tenantId);
}

export async function getAnomalies(
  tenantId: string,
  params?: { status?: string; severity?: string; kind?: string; limit?: number }
): Promise<{ anomalies: Anomaly[] }> {
  const searchParams = new URLSearchParams();
  if (params?.status) searchParams.set('status', params.status);
  if (params?.severity) searchParams.set('severity', params.severity);
  if (params?.kind) searchParams.set('kind', params.kind);
  if (params?.limit) searchParams.set('limit', params.limit.toString());

  const query = searchParams.toString();
  return apiCall(`/anomalies${query ? `?${query}` : ''}`, tenantId);
}

export async function updateAnomalyStatus(
  tenantId: string,
  anomalyId: number,
  status: string
): Promise<void> {
  await apiCall(`/anomalies/${anomalyId}/status`, tenantId, {
    method: 'PATCH',
    body: JSON.stringify({ status }),
  });
}

export async function getIncidents(
  tenantId: string,
  params?: { status?: string; severity?: string; limit?: number }
): Promise<{ incidents: Incident[] }> {
  const searchParams = new URLSearchParams();
  if (params?.status) searchParams.set('status', params.status);
  if (params?.severity) searchParams.set('severity', params.severity);
  if (params?.limit) searchParams.set('limit', params.limit.toString());

  const query = searchParams.toString();
  return apiCall(`/incidents${query ? `?${query}` : ''}`, tenantId);
}

export async function getIncidentDetail(tenantId: string, incidentId: number): Promise<Incident> {
  return apiCall<Incident>(`/incidents/${incidentId}`, tenantId);
}

export async function updateIncident(
  tenantId: string,
  incidentId: number,
  update: { status?: string; note?: string }
): Promise<Incident> {
  return apiCall<Incident>(`/incidents/${incidentId}`, tenantId, {
    method: 'PATCH',
    body: JSON.stringify(update),
  });
}

export async function getRemediations(
  tenantId: string,
  params?: { status?: string; limit?: number }
): Promise<{ actions: RemediationAction[] }> {
  const searchParams = new URLSearchParams();
  if (params?.status) searchParams.set('status', params.status);
  if (params?.limit) searchParams.set('limit', params.limit.toString());

  const query = searchParams.toString();
  return apiCall(`/remediations${query ? `?${query}` : ''}`, tenantId);
}

export async function proposeRemediation(
  tenantId: string,
  request: {
    playbookKey: string;
    resourceId?: string;
    title?: string;
    reason: string;
    parametersJson?: string;
  }
): Promise<RemediationAction> {
  return apiCall<RemediationAction>('/remediations', tenantId, {
    method: 'POST',
    body: JSON.stringify(request),
  });
}

export async function approveRemediation(
  tenantId: string,
  actionId: number
): Promise<RemediationAction> {
  return apiCall<RemediationAction>(`/remediations/${actionId}/approve`, tenantId, {
    method: 'POST',
  });
}

export async function rejectRemediation(
  tenantId: string,
  actionId: number,
  reason?: string
): Promise<RemediationAction> {
  return apiCall<RemediationAction>(`/remediations/${actionId}/reject`, tenantId, {
    method: 'POST',
    body: JSON.stringify({ reason }),
  });
}

export async function getPlaybooks(tenantId: string): Promise<{ playbooks: Playbook[] }> {
  return apiCall(`/remediations/playbooks`, tenantId);
}

export async function getCloudAccounts(tenantId: string): Promise<{ accounts: CloudAccount[] }> {
  return apiCall(`/cloud-accounts`, tenantId);
}

export async function getCloudOrgs(tenantId: string): Promise<{ orgs: CloudOrg[] }> {
  return apiCall(`/cloud-orgs`, tenantId);
}

/** Instance environment, app version, and commit SHA from the anonymous health endpoint. */
export async function getPlatformInfo(): Promise<{ environment: string; version: string; commit: string | null }> {
  try {
    const res = await fetch(`${API_BASE}/health`);
    const data = await res.json();
    return {
      environment: data.environment ?? 'Development',
      version: data.version ?? '1.0.0',
      commit: data.commit ?? null,
    };
  } catch {
    return { environment: 'Development', version: '1.0.0', commit: null };
  }
}

/** On-demand inventory collection for one AWS account / GCP project. */
export async function collectCloudAccount(tenantId: string, accountId: string): Promise<{ resourcesCollected: number }> {
  return apiCall(`/cloud-accounts/${accountId}/collect`, tenantId, { method: 'POST' });
}

export async function createCloudOrg(
  tenantId: string,
  request: { provider: string; name: string; externalId?: string }
): Promise<CloudOrg> {
  return apiCall<CloudOrg>('/cloud-orgs', tenantId, {
    method: 'POST',
    body: JSON.stringify(request),
  });
}

/** Suspend / reactivate an AWS or GCP organization (parity with Azure tenant status). */
export async function updateCloudOrgStatus(
  tenantId: string,
  orgId: string,
  status: string
): Promise<{ orgId: string; status: string }> {
  return apiCall(`/cloud-orgs/${orgId}/status`, tenantId, {
    method: 'PATCH',
    body: JSON.stringify({ status }),
  });
}

export async function linkCloudAccount(
  tenantId: string,
  request: {
    orgId: string;
    externalId: string;
    displayName: string;
    credentialJson?: string;
    regions?: string[];
  }
): Promise<CloudAccount> {
  return apiCall<CloudAccount>('/cloud-accounts', tenantId, {
    method: 'POST',
    body: JSON.stringify(request),
  });
}

// ============================================================================
// Namespace export for convenient usage: import { api } from '@/lib/api'
// ============================================================================

export const api = {
  getOrganizations,
  createOrganization,
  connectAzureTenant,
  getTenants,
  onboardTenant,
  updateTenantStatus,
  getInventoryResources,
  getResourceDetail,
  getInventorySummary,
  triggerSnapshot,
  getChanges,
  getChangeTimeline,
  getAdvisorRecommendations,
  getDefenderFindings,
  getPolicyCompliance,
  queryAi,
  getDashboard,
  getOpsSummary,
  getAnomalies,
  updateAnomalyStatus,
  getIncidents,
  getIncidentDetail,
  updateIncident,
  getRemediations,
  proposeRemediation,
  approveRemediation,
  rejectRemediation,
  getPlaybooks,
  getCloudAccounts,
  getCloudOrgs,
  createCloudOrg,
  updateCloudOrgStatus,
  linkCloudAccount,
  collectCloudAccount,
  getPlatformInfo,
  getMe,
  getSystemSettings,
  updateSystemSettings,
  getAllUsers,
  createUser,
  updateUser,
  getOrgUsers,
  addOrgUser,
  updateOrgUserRole,
  removeOrgUser,
  getOrgSso,
  updateOrgSso,
};
