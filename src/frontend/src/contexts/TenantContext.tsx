'use client';

import React, { createContext, useContext, useState, useCallback, useEffect, type ReactNode } from 'react';
import type { Organization } from '../lib/types';
import { useOrganizations } from '../lib/hooks';

/**
 * Workspace context.
 *
 * The workspace an operator selects is an **Organization** (org_id), which is the
 * same GUID used as the RLS `tenant_id` everywhere. To avoid churning every page
 * that reads `tenantId` / `currentTenant`, this context keeps that surface (backed
 * by the selected org) AND exposes the richer `organizations` / `currentOrg`.
 */

interface WorkspaceRef {
  tenantId: string;
  displayName: string;
  status: string;
}

interface TenantContextState {
  /** All organizations (workspaces) available */
  organizations: Organization[];
  /** Currently selected organization */
  currentOrg: Organization | null;
  /** Back-compat: workspaces as tenant-like refs (tenantId = org_id) */
  tenants: WorkspaceRef[];
  /** Back-compat: the selected workspace as a tenant-like ref */
  currentTenant: WorkspaceRef | null;
  /** Current workspace ID (org_id === RLS tenant_id) */
  tenantId: string | null;
  /** Select a workspace/organization by ID */
  selectTenant: (id: string) => void;
  /** Re-fetch organizations (e.g. after creating one or connecting a cloud) */
  refreshOrganizations: () => void;
  isLoading: boolean;
  error: Error | undefined;
}

const TenantContext = createContext<TenantContextState | undefined>(undefined);

const WORKSPACE_STORAGE_KEY = 'aim-selected-tenant';

function toRef(org: Organization): WorkspaceRef {
  return { tenantId: org.orgId, displayName: org.name, status: org.status };
}

export function TenantProvider({ children }: { children: ReactNode }) {
  const { data: organizations, error, isLoading, mutate } = useOrganizations();
  const [selectedId, setSelectedId] = useState<string | null>(() => {
    if (typeof window !== 'undefined') {
      return sessionStorage.getItem(WORKSPACE_STORAGE_KEY);
    }
    return null;
  });

  const activeOrgs = organizations?.filter((o) => o.status === 'active') ?? [];

  // Auto-select the first organization if none is selected (or the stored one is gone)
  useEffect(() => {
    if (activeOrgs.length === 0) return;
    if (!selectedId || !activeOrgs.some((o) => o.orgId === selectedId)) {
      setSelectedId(activeOrgs[0].orgId);
    }
  }, [activeOrgs, selectedId]);

  const currentOrg = activeOrgs.find((o) => o.orgId === selectedId) ?? null;

  const selectTenant = useCallback((id: string) => {
    setSelectedId(id);
    if (typeof window !== 'undefined') {
      sessionStorage.setItem(WORKSPACE_STORAGE_KEY, id);
    }
  }, []);

  const refreshOrganizations = useCallback(() => { mutate(); }, [mutate]);

  return (
    <TenantContext.Provider
      value={{
        organizations: activeOrgs,
        currentOrg,
        tenants: activeOrgs.map(toRef),
        currentTenant: currentOrg ? toRef(currentOrg) : null,
        tenantId: currentOrg?.orgId ?? null,
        selectTenant,
        refreshOrganizations,
        isLoading,
        error,
      }}
    >
      {children}
    </TenantContext.Provider>
  );
}

export function useTenantContext(): TenantContextState {
  const ctx = useContext(TenantContext);
  if (!ctx) {
    throw new Error('useTenantContext must be used within <TenantProvider>');
  }
  return ctx;
}

/** Alias for useTenantContext — returns the selected workspace ID directly */
export function useTenant(): { selectedTenant: string | null; tenants: TenantContextState['tenants'] } {
  const ctx = useTenantContext();
  return {
    selectedTenant: ctx.tenantId,
    tenants: ctx.tenants,
  };
}
