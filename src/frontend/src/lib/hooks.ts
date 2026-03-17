import useSWR from 'swr';
import {
  getInventoryResources,
  getInventorySummary,
  getResourceDetail,
  getChanges,
  getChangeTimeline,
  getAdvisorRecommendations,
  getDefenderFindings,
  getPolicyCompliance,
  getTenants,
  getDashboard,
} from './api';
import type { TenantSummary, InventoryResource, TenantDashboard } from './types';

/**
 * SWR hooks for data fetching with caching, revalidation, and error handling.
 * All hooks require a tenantId to scope data correctly.
 */

// Global SWR config (set in provider)
export const swrConfig = {
  revalidateOnFocus: false,
  shouldRetryOnError: true,
  errorRetryCount: 3,
  dedupingInterval: 5000,
};

// ---- Tenant hooks ----

export function useTenants() {
  return useSWR<TenantSummary[]>('tenants', () => getTenants(), {
    revalidateOnFocus: true,
    refreshInterval: 60_000,
  });
}

// ---- Inventory hooks ----

export function useInventoryResources(
  tenantId: string | null,
  params?: {
    resourceType?: string;
    subscriptionId?: string;
    resourceGroup?: string;
    offset?: number;
    limit?: number;
  }
) {
  const key = tenantId
    ? ['inventory', tenantId, JSON.stringify(params || {})]
    : null;

  return useSWR(key, () => getInventoryResources(tenantId!, params));
}

export function useInventorySummary(tenantId: string | null) {
  return useSWR(
    tenantId ? ['inventory-summary', tenantId] : null,
    () => getInventorySummary(tenantId!)
  );
}

export function useResourceDetail(tenantId: string | null, resourceId: string | null) {
  return useSWR<InventoryResource>(
    tenantId && resourceId ? ['resource-detail', tenantId, resourceId] : null,
    () => getResourceDetail(tenantId!, resourceId!)
  );
}

// ---- Dashboard hooks ----

export function useDashboard(tenantId: string | null) {
  return useSWR<TenantDashboard>(
    tenantId ? ['dashboard', tenantId] : null,
    () => getDashboard(tenantId!),
    { refreshInterval: 60_000 }
  );
}

// ---- Change hooks ----

export function useChanges(
  tenantId: string | null,
  params?: {
    from?: string;
    to?: string;
    resourceId?: string;
    classification?: string;
    offset?: number;
    limit?: number;
  }
) {
  const key = tenantId
    ? ['changes', tenantId, JSON.stringify(params || {})]
    : null;

  return useSWR(key, () => getChanges(tenantId!, params));
}

export function useChangeTimeline(
  tenantId: string | null,
  from: string,
  to: string
) {
  return useSWR(
    tenantId ? ['change-timeline', tenantId, from, to] : null,
    () => getChangeTimeline(tenantId!, from, to)
  );
}

// ---- Recommendation hooks ----

export function useAdvisorRecommendations(
  tenantId: string | null,
  params?: { category?: string; status?: string; limit?: number }
) {
  const key = tenantId
    ? ['advisor', tenantId, JSON.stringify(params || {})]
    : null;

  return useSWR(key, () => getAdvisorRecommendations(tenantId!, params));
}

export function useDefenderFindings(
  tenantId: string | null,
  params?: { severity?: string; status?: string; limit?: number }
) {
  const key = tenantId
    ? ['defender', tenantId, JSON.stringify(params || {})]
    : null;

  return useSWR(key, () => getDefenderFindings(tenantId!, params));
}

export function usePolicyCompliance(
  tenantId: string | null,
  params?: { state?: string; limit?: number }
) {
  const key = tenantId
    ? ['policy', tenantId, JSON.stringify(params || {})]
    : null;

  return useSWR(key, () => getPolicyCompliance(tenantId!, params));
}
