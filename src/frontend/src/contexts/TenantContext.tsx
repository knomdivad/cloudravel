'use client';

import React, { createContext, useContext, useState, useCallback, useEffect, type ReactNode } from 'react';
import type { TenantSummary } from '../lib/types';
import { useTenants } from '../lib/hooks';

interface TenantContextState {
  /** All tenants the user has access to */
  tenants: TenantSummary[];
  /** Currently selected tenant */
  currentTenant: TenantSummary | null;
  /** Current tenant ID (convenience accessor) */
  tenantId: string | null;
  /** Select a tenant by ID */
  selectTenant: (tenantId: string) => void;
  /** Loading state */
  isLoading: boolean;
  /** Error state */
  error: Error | undefined;
}

const TenantContext = createContext<TenantContextState | undefined>(undefined);

const TENANT_STORAGE_KEY = 'aim-selected-tenant';

export function TenantProvider({ children }: { children: ReactNode }) {
  const { data: tenants, error, isLoading } = useTenants();
  const [selectedId, setSelectedId] = useState<string | null>(() => {
    if (typeof window !== 'undefined') {
      return sessionStorage.getItem(TENANT_STORAGE_KEY);
    }
    return null;
  });

  const activeTenants = tenants?.filter((t) => t.status === 'active' || t.status === 'degraded') ?? [];

  // Auto-select first tenant if none selected
  useEffect(() => {
    if (!selectedId && activeTenants.length > 0) {
      setSelectedId(activeTenants[0].tenantId);
    }
  }, [activeTenants, selectedId]);

  const currentTenant = activeTenants.find((t) => t.tenantId === selectedId) ?? null;

  const selectTenant = useCallback((id: string) => {
    setSelectedId(id);
    if (typeof window !== 'undefined') {
      sessionStorage.setItem(TENANT_STORAGE_KEY, id);
    }
  }, []);

  return (
    <TenantContext.Provider
      value={{
        tenants: activeTenants,
        currentTenant,
        tenantId: currentTenant?.tenantId ?? null,
        selectTenant,
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

/** Alias for useTenantContext — returns the selected tenant ID directly */
export function useTenant(): { selectedTenant: string | null; tenants: TenantContextState['tenants'] } {
  const ctx = useTenantContext();
  return {
    selectedTenant: ctx.currentTenant?.tenantId ?? null,
    tenants: ctx.tenants,
  };
}
