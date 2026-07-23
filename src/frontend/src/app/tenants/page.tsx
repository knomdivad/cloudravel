'use client';

import { useState, useEffect, useCallback } from 'react';
import { api } from '@/lib/api';
import { useTenantContext } from '@/contexts/TenantContext';
import type { CloudOrg, CloudAccount } from '@/lib/types';

interface TenantSummary {
  tenantId: string;
  displayName: string;
  azureTenantId: string;
  status: string;
  resourceCount?: number;
  lastSnapshotAt?: string;
  changes24H?: number;
}

type ToastType = 'success' | 'error';

const PROVIDER_BADGES: Record<string, string> = {
  azure: 'bg-blue-100 text-blue-700',
  aws: 'bg-amber-100 text-amber-800',
  gcp: 'bg-emerald-100 text-emerald-700',
};

const statusBadge = (status: string) => {
  const styles: Record<string, string> = {
    active: 'bg-green-100 text-green-800',
    connected: 'bg-green-100 text-green-800',
    degraded: 'bg-yellow-100 text-yellow-800',
    suspended: 'bg-red-100 text-red-800',
    disconnected: 'bg-red-100 text-red-800',
    offboarded: 'bg-gray-100 text-gray-800',
  };
  return styles[status.toLowerCase()] || 'bg-gray-100 text-gray-800';
};

export default function CloudsPage() {
  const { tenantId, currentTenant } = useTenantContext();

  const [tenants, setTenants] = useState<TenantSummary[]>([]);
  const [orgs, setOrgs] = useState<CloudOrg[]>([]);
  const [loading, setLoading] = useState(true);
  const [showAdd, setShowAdd] = useState(false);
  const [addAccountFor, setAddAccountFor] = useState<CloudOrg | null>(null);
  const [toast, setToast] = useState<{ message: string; type: ToastType } | null>(null);
  const [statusAction, setStatusAction] = useState<{ tenant: TenantSummary; newStatus: string } | null>(null);
  const [updatingStatus, setUpdatingStatus] = useState(false);
  const [collectingTenant, setCollectingTenant] = useState<string | null>(null);
  const [collectingAccount, setCollectingAccount] = useState<string | null>(null);
  const [isDev, setIsDev] = useState(true);

  useEffect(() => { api.getPlatformInfo().then(p => setIsDev(p.environment.toLowerCase() !== 'production')).catch(() => {}); }, []);

  const showToast = useCallback((message: string, type: ToastType) => {
    setToast({ message, type });
    setTimeout(() => setToast(null), 5000);
  }, []);

  const fetchTenants = useCallback(async () => {
    try {
      const data = await api.getTenants();
      setTenants(data || []);
    } catch {
      showToast('Failed to load Azure tenants.', 'error');
    } finally {
      setLoading(false);
    }
  }, [showToast]);

  const fetchOrgs = useCallback(async () => {
    if (!tenantId) { setOrgs([]); return; }
    try {
      const data = await api.getCloudOrgs(tenantId);
      setOrgs(data.orgs || []);
    } catch {
      /* non-fatal */
    }
  }, [tenantId]);

  useEffect(() => { fetchTenants(); }, [fetchTenants]);
  useEffect(() => { fetchOrgs(); }, [fetchOrgs]);

  const handleStatusUpdate = async () => {
    if (!statusAction) return;
    setUpdatingStatus(true);
    try {
      await api.updateTenantStatus(statusAction.tenant.tenantId, statusAction.newStatus);
      showToast(`${statusAction.tenant.displayName} → ${statusAction.newStatus}.`, 'success');
      setStatusAction(null);
      fetchTenants();
    } catch (err) {
      showToast(err instanceof Error ? err.message : 'Failed to update status.', 'error');
    } finally {
      setUpdatingStatus(false);
    }
  };

  const handleCollectInventory = async (tenant: TenantSummary) => {
    setCollectingTenant(tenant.tenantId);
    try {
      await api.triggerSnapshot(tenant.tenantId);
      showToast(`Inventory collected for "${tenant.displayName}".`, 'success');
      fetchTenants();
    } catch (err) {
      showToast(err instanceof Error ? err.message : 'Failed to collect inventory.', 'error');
    } finally {
      setCollectingTenant(null);
    }
  };

  const handleCollectAccount = async (acct: CloudAccount) => {
    setCollectingAccount(acct.accountId);
    try {
      const res = await api.collectCloudAccount(tenantId!, acct.accountId);
      showToast(`Collected ${res.resourcesCollected} resources from "${acct.displayName}".`, 'success');
      fetchOrgs();
    } catch (err) {
      showToast(err instanceof Error ? err.message : 'Collection failed.', 'error');
    } finally {
      setCollectingAccount(null);
    }
  };

  const getStatusActions = (status: string): { label: string; value: string; destructive?: boolean }[] => {
    switch (status) {
      case 'active': return [{ label: 'Suspend', value: 'suspended', destructive: true }, { label: 'Offboard', value: 'offboarded', destructive: true }];
      case 'degraded': return [{ label: 'Reactivate', value: 'active' }, { label: 'Suspend', value: 'suspended', destructive: true }];
      case 'suspended': return [{ label: 'Reactivate', value: 'active' }, { label: 'Offboard', value: 'offboarded', destructive: true }];
      default: return [];
    }
  };

  if (loading) {
    return <div className="flex items-center justify-center h-64"><div className="animate-spin rounded-full h-8 w-8 border-b-2 border-blue-600" /></div>;
  }

  const awsOrgs = orgs.filter(o => o.provider.toLowerCase() === 'aws');
  const gcpOrgs = orgs.filter(o => o.provider.toLowerCase() === 'gcp');
  const totalClouds = tenants.length + orgs.length;

  return (
    <div className="space-y-8">
      {toast && (
        <div className={`fixed top-4 right-4 z-[60] max-w-sm px-4 py-3 rounded-lg shadow-lg text-sm font-medium flex items-center gap-2 ${
          toast.type === 'success' ? 'bg-green-50 border border-green-200 text-green-800' : 'bg-red-50 border border-red-200 text-red-800'
        }`}>
          <span>{toast.type === 'success' ? '✓' : '✕'}</span>
          <span className="flex-1">{toast.message}</span>
          <button onClick={() => setToast(null)} className="ml-2 opacity-60 hover:opacity-100">✕</button>
        </div>
      )}

      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-bold text-gray-900">Clouds</h1>
          <p className="text-sm text-gray-500 mt-1">
            Azure tenants, AWS organizations, and GCP organizations — independent peers &middot; {totalClouds} connected
          </p>
        </div>
        <button onClick={() => setShowAdd(true)} className="px-4 py-2 bg-blue-600 text-white rounded-lg hover:bg-blue-700 text-sm font-medium">
          + Add Cloud
        </button>
      </div>

      {showAdd && (
        <AddCloudModal
          tenantId={tenantId}
          onClose={() => setShowAdd(false)}
          onAdded={(msg) => { showToast(msg, 'success'); setShowAdd(false); fetchTenants(); fetchOrgs(); }}
        />
      )}

      {addAccountFor && (
        <AddAccountModal
          tenantId={tenantId!}
          org={addAccountFor}
          onClose={() => setAddAccountFor(null)}
          onAdded={(msg) => { showToast(msg, 'success'); setAddAccountFor(null); fetchOrgs(); }}
        />
      )}

      {/* Status change confirm */}
      {statusAction && (
        <div className="fixed inset-0 bg-black/50 flex items-center justify-center z-50 p-4">
          <div className="bg-white rounded-lg shadow-xl max-w-md w-full p-6">
            <h3 className="text-lg font-semibold text-gray-900 mb-2">
              {statusAction.newStatus === 'active' ? 'Reactivate' : statusAction.newStatus} Tenant
            </h3>
            <p className="text-sm text-gray-600 mb-1">
              Change <strong>{statusAction.tenant.displayName}</strong> to <strong>{statusAction.newStatus}</strong>?
            </p>
            <div className="flex justify-end gap-3 mt-6">
              <button onClick={() => setStatusAction(null)} className="px-4 py-2 text-gray-700 border border-gray-300 rounded-lg text-sm hover:bg-gray-50">Cancel</button>
              <button onClick={handleStatusUpdate} disabled={updatingStatus}
                className={`px-4 py-2 rounded-lg text-sm text-white flex items-center gap-2 disabled:opacity-50 ${statusAction.newStatus === 'active' ? 'bg-green-600 hover:bg-green-700' : 'bg-red-600 hover:bg-red-700'}`}>
                {updatingStatus && <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white" />}
                {updatingStatus ? 'Updating...' : 'Confirm'}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Azure tenants */}
      {tenants.length > 0 && (
        <section>
          <h2 className="text-sm font-semibold text-gray-700 uppercase tracking-wide mb-3">Azure</h2>
          <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
            {tenants.map(tenant => {
              const actions = getStatusActions(tenant.status);
              return (
                <div key={tenant.tenantId} className="bg-white rounded-lg border border-gray-200 p-5 hover:shadow-md transition-shadow">
                  <div className="flex items-start justify-between mb-3">
                    <div className="flex items-center gap-2 min-w-0">
                      <span className={`text-xs px-2 py-0.5 rounded-full font-medium ${PROVIDER_BADGES.azure}`}>Azure</span>
                      <h3 className="font-semibold text-gray-900 truncate">{tenant.displayName}</h3>
                    </div>
                    <span className={`px-2 py-0.5 rounded-full text-xs font-medium whitespace-nowrap ${statusBadge(tenant.status)}`}>{tenant.status}</span>
                  </div>
                  <p className="text-xs text-gray-500 font-mono mb-4 truncate">{tenant.azureTenantId}</p>
                  <div className="grid grid-cols-3 gap-2 text-center mb-4">
                    <div><p className="text-lg font-bold text-gray-900">{tenant.resourceCount ?? '—'}</p><p className="text-xs text-gray-500">Resources</p></div>
                    <div><p className="text-lg font-bold text-gray-900">{tenant.changes24H ?? '—'}</p><p className="text-xs text-gray-500">Changes 24h</p></div>
                    <div><p className="text-xs text-gray-500 mt-1">{tenant.lastSnapshotAt ? new Date(tenant.lastSnapshotAt).toLocaleDateString() : 'No snapshot'}</p><p className="text-xs text-gray-500">Last Snapshot</p></div>
                  </div>
                  {actions.length > 0 && (
                    <div className="flex gap-2 pt-3 border-t border-gray-100">
                      {actions.map(action => (
                        <button key={action.value} onClick={() => setStatusAction({ tenant, newStatus: action.value })}
                          className={`flex-1 px-3 py-1.5 rounded text-xs font-medium transition-colors ${action.destructive ? 'text-red-600 border border-red-200 hover:bg-red-50' : 'text-green-600 border border-green-200 hover:bg-green-50'}`}>
                          {action.label}
                        </button>
                      ))}
                    </div>
                  )}
                  {tenant.status === 'active' && (
                    <div className="pt-2">
                      <button onClick={() => handleCollectInventory(tenant)} disabled={isDev || collectingTenant === tenant.tenantId}
                        title={isDev ? 'Inventory collection is disabled in development' : 'Collect inventory now'}
                        className="w-full px-3 py-1.5 rounded text-xs font-medium transition-colors text-blue-600 border border-blue-200 hover:bg-blue-50 disabled:opacity-40 disabled:cursor-not-allowed flex items-center justify-center gap-2">
                        {collectingTenant === tenant.tenantId && <div className="animate-spin rounded-full h-3 w-3 border-b-2 border-blue-600" />}
                        {collectingTenant === tenant.tenantId ? 'Collecting...' : 'Collect Inventory'}
                      </button>
                    </div>
                  )}
                </div>
              );
            })}
          </div>
        </section>
      )}

      {/* AWS orgs */}
      {awsOrgs.length > 0 && (
        <OrgSection label="AWS" orgs={awsOrgs} onAddAccount={setAddAccountFor}
          onCollect={handleCollectAccount} collecting={collectingAccount} isDev={isDev} />
      )}

      {/* GCP orgs */}
      {gcpOrgs.length > 0 && (
        <OrgSection label="Google Cloud" orgs={gcpOrgs} onAddAccount={setAddAccountFor}
          onCollect={handleCollectAccount} collecting={collectingAccount} isDev={isDev} />
      )}

      {totalClouds === 0 && (
        <div className="text-center py-12 bg-white rounded-lg border border-gray-200">
          <p className="text-gray-500 mb-1">No clouds connected yet</p>
          <p className="text-sm text-gray-400 mb-4">Connect an Azure tenant, AWS organization, or GCP organization to start monitoring.</p>
          <button onClick={() => setShowAdd(true)} className="px-4 py-2 bg-blue-600 text-white rounded-lg hover:bg-blue-700 text-sm">Add First Cloud</button>
        </div>
      )}
    </div>
  );
}

function OrgSection({ label, orgs, onAddAccount, onCollect, collecting, isDev }: {
  label: string; orgs: CloudOrg[]; onAddAccount: (o: CloudOrg) => void;
  onCollect: (a: CloudAccount) => void; collecting: string | null; isDev: boolean;
}) {
  const accountNoun = label === 'AWS' ? 'account' : 'project';
  return (
    <section>
      <h2 className="text-sm font-semibold text-gray-700 uppercase tracking-wide mb-3">{label}</h2>
      <div className="space-y-4">
        {orgs.map(org => (
          <div key={org.orgId} className="bg-white rounded-lg border border-gray-200 p-5">
            <div className="flex items-start justify-between mb-3">
              <div className="flex items-center gap-2 min-w-0">
                <span className={`text-xs px-2 py-0.5 rounded-full font-medium ${PROVIDER_BADGES[org.provider.toLowerCase()] ?? ''}`}>{org.provider.toUpperCase()}</span>
                <div className="min-w-0">
                  <h3 className="font-semibold text-gray-900 truncate">{org.name}</h3>
                  {org.externalId && <p className="text-xs text-gray-400 font-mono truncate">Org ID: {org.externalId}</p>}
                </div>
              </div>
              <button onClick={() => onAddAccount(org)} className="text-xs font-medium text-blue-600 hover:text-blue-700 whitespace-nowrap">
                + Add {accountNoun}
              </button>
            </div>

            {org.accounts.length === 0 ? (
              <p className="text-sm text-gray-400 py-2">No {accountNoun}s yet — add one to start collecting inventory.</p>
            ) : (
              <div className="divide-y divide-gray-100 border-t border-gray-100">
                {org.accounts.map(acct => (
                  <div key={acct.accountId} className="py-2.5 flex items-center justify-between gap-3">
                    <div className="min-w-0">
                      <p className="text-sm font-medium text-gray-900 truncate">{acct.displayName}</p>
                      <p className="text-xs text-gray-400 font-mono truncate">
                        {acct.externalId}{acct.regions && acct.regions.length > 0 ? ` · ${acct.regions.join(', ')}` : ''}
                      </p>
                      {acct.lastError && <p className="text-xs text-red-600 truncate">{acct.lastError}</p>}
                    </div>
                    <div className="flex items-center gap-3 flex-shrink-0">
                      <div className="text-right">
                        <span className={`px-2 py-0.5 rounded-full text-xs font-medium ${statusBadge(acct.status)}`}>{acct.status}</span>
                        <p className="text-xs text-gray-400 mt-1">
                          {acct.lastInventoryAt ? new Date(acct.lastInventoryAt).toLocaleDateString() : 'never'}
                        </p>
                      </div>
                      <button
                        onClick={() => onCollect(acct)}
                        disabled={isDev || collecting === acct.accountId}
                        title={isDev ? 'Inventory collection is disabled in development' : 'Collect inventory now'}
                        className="px-3 py-1.5 rounded text-xs font-medium text-blue-600 border border-blue-200 hover:bg-blue-50 disabled:opacity-40 disabled:cursor-not-allowed flex items-center gap-1.5 whitespace-nowrap"
                      >
                        {collecting === acct.accountId && <div className="animate-spin rounded-full h-3 w-3 border-b-2 border-blue-600" />}
                        {collecting === acct.accountId ? 'Collecting...' : 'Collect'}
                      </button>
                    </div>
                  </div>
                ))}
              </div>
            )}
          </div>
        ))}
      </div>
    </section>
  );
}

// ============================================================================
// Add Cloud modal — Azure tenant, or a new AWS/GCP org
// ============================================================================

function AddCloudModal({ tenantId, onClose, onAdded }: {
  tenantId: string | null;
  onClose: () => void;
  onAdded: (message: string) => void;
}) {
  const [provider, setProvider] = useState<'azure' | 'aws' | 'gcp'>('azure');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Azure fields
  const [displayName, setDisplayName] = useState('');
  const [azureTenantId, setAzureTenantId] = useState('');
  const [onboardingMethod, setOnboardingMethod] = useState<'lighthouse' | 'app_registration'>('lighthouse');
  const [clientId, setClientId] = useState('');
  const [clientSecret, setClientSecret] = useState('');
  // Org (aws/gcp) fields
  const [orgName, setOrgName] = useState('');
  const [orgExternalId, setOrgExternalId] = useState('');

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setBusy(true); setError(null);
    try {
      if (provider === 'azure') {
        await api.onboardTenant({
          displayName, azureTenantId, onboardingMethod,
          clientId: onboardingMethod === 'app_registration' ? clientId : undefined,
          clientSecret: onboardingMethod === 'app_registration' ? clientSecret || undefined : undefined,
        });
        onAdded(`Azure tenant "${displayName}" onboarded.`);
      } else {
        if (!tenantId) { setError('No workspace selected. Add or select an Azure tenant first to establish the workspace.'); setBusy(false); return; }
        await api.createCloudOrg(tenantId, { provider, name: orgName, externalId: orgExternalId || undefined });
        onAdded(`${provider.toUpperCase()} organization "${orgName}" created. Add accounts to it next.`);
      }
    } catch (err: any) {
      setError(err.message ?? 'Failed to add cloud.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="fixed inset-0 bg-black/50 flex items-center justify-center z-50 p-4">
      <div className="bg-white rounded-lg shadow-xl max-w-lg w-full max-h-[90vh] overflow-y-auto p-6">
        <div className="flex items-center justify-between mb-4">
          <h2 className="text-lg font-semibold text-gray-900">Add Cloud</h2>
          <button onClick={onClose} className="text-gray-400 hover:text-gray-600">
            <svg className="h-5 w-5" fill="none" viewBox="0 0 24 24" stroke="currentColor"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" /></svg>
          </button>
        </div>

        <div className="mb-4">
          <label className="block text-sm font-medium text-gray-700 mb-1">Provider</label>
          <div className="grid grid-cols-3 gap-2">
            {(['azure', 'aws', 'gcp'] as const).map(p => (
              <button key={p} type="button" onClick={() => setProvider(p)}
                className={`px-3 py-2 rounded-lg border text-sm font-medium transition-colors ${provider === p ? 'border-blue-500 bg-blue-50 text-blue-700' : 'border-gray-300 text-gray-600 hover:bg-gray-50'}`}>
                {p === 'azure' ? 'Azure' : p === 'aws' ? 'AWS' : 'Google Cloud'}
              </button>
            ))}
          </div>
          <p className="text-xs text-gray-400 mt-1">
            {provider === 'azure' ? 'Onboard an Azure tenant.' : `Create a new ${provider.toUpperCase()} organization — a top-level grouping for its ${provider === 'aws' ? 'accounts' : 'projects'}. Independent of Azure.`}
          </p>
        </div>

        {error && <div className="mb-4 p-3 bg-red-50 border border-red-200 rounded-lg text-sm text-red-700">{error}</div>}

        <form onSubmit={submit} className="space-y-4">
          {provider === 'azure' ? (
            <>
              <Field label="Display Name" required value={displayName} onChange={setDisplayName} placeholder="Contoso Corp" />
              <Field label="Azure Tenant ID" required mono value={azureTenantId} onChange={setAzureTenantId}
                placeholder="00000000-0000-0000-0000-000000000000"
                pattern="[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}" />
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">Onboarding Method</label>
                <div className="grid grid-cols-2 gap-2">
                  {(['lighthouse', 'app_registration'] as const).map(m => (
                    <button key={m} type="button" onClick={() => setOnboardingMethod(m)}
                      className={`px-3 py-2 rounded-lg border text-sm font-medium transition-colors ${onboardingMethod === m ? 'border-blue-500 bg-blue-50 text-blue-700' : 'border-gray-300 text-gray-600 hover:bg-gray-50'}`}>
                      {m === 'lighthouse' ? 'Azure Lighthouse' : 'App Registration'}
                    </button>
                  ))}
                </div>
              </div>
              {onboardingMethod === 'app_registration' && (
                <div className="space-y-3 p-4 bg-gray-50 rounded-lg border border-gray-200">
                  <Field label="Client ID" required mono value={clientId} onChange={setClientId} placeholder="Application (client) ID"
                    pattern="[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}" />
                  <Field label="Client Secret" type="password" mono value={clientSecret} onChange={setClientSecret} placeholder="Client secret value" />
                </div>
              )}
            </>
          ) : (
            <>
              <Field label={`${provider.toUpperCase()} Organization Name`} required value={orgName} onChange={setOrgName}
                placeholder={provider === 'aws' ? 'Contoso AWS Organization' : 'Contoso GCP Organization'} />
              <Field label="Organization ID (optional)" mono value={orgExternalId} onChange={setOrgExternalId}
                placeholder={provider === 'aws' ? 'o-abc123def4' : '849021304719'} />
            </>
          )}

          <div className="flex justify-end gap-3 pt-4 border-t border-gray-200">
            <button type="button" onClick={onClose} className="px-4 py-2 text-gray-700 border border-gray-300 rounded-lg text-sm hover:bg-gray-50">Cancel</button>
            <button type="submit" disabled={busy} className="px-4 py-2 bg-blue-600 text-white rounded-lg text-sm hover:bg-blue-700 disabled:opacity-50 flex items-center gap-2">
              {busy && <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white" />}
              {busy ? 'Working...' : provider === 'azure' ? 'Onboard Tenant' : 'Create Organization'}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}

// ============================================================================
// Add Account modal — an AWS account / GCP project under an org
// ============================================================================

function AddAccountModal({ tenantId, org, onClose, onAdded }: {
  tenantId: string;
  org: CloudOrg;
  onClose: () => void;
  onAdded: (message: string) => void;
}) {
  const isAws = org.provider.toLowerCase() === 'aws';
  const noun = isAws ? 'account' : 'project';

  const [externalId, setExternalId] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [regions, setRegions] = useState('');
  // AWS creds
  const [accessKeyId, setAccessKeyId] = useState('');
  const [secretAccessKey, setSecretAccessKey] = useState('');
  const [sessionToken, setSessionToken] = useState('');
  const [defaultRegion, setDefaultRegion] = useState('us-east-1');
  // GCP creds
  const [serviceAccountJson, setServiceAccountJson] = useState('');

  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setBusy(true); setError(null);
    let credentialJson: string | undefined;
    try {
      if (isAws) {
        if (accessKeyId && secretAccessKey) {
          credentialJson = JSON.stringify({
            accessKeyId: accessKeyId.trim(), secretAccessKey: secretAccessKey.trim(),
            sessionToken: sessionToken.trim() || undefined, defaultRegion: defaultRegion.trim() || 'us-east-1',
          });
        }
      } else if (serviceAccountJson.trim()) {
        JSON.parse(serviceAccountJson);
        credentialJson = serviceAccountJson.trim();
      }
    } catch {
      setError('The GCP service account key must be valid JSON.'); setBusy(false); return;
    }

    const regionList = regions.split(/[,\n]+/).map(r => r.trim()).filter(Boolean);
    try {
      await api.linkCloudAccount(tenantId, {
        orgId: org.orgId,
        externalId: externalId.trim(),
        displayName: displayName.trim(),
        credentialJson,
        regions: regionList.length > 0 ? regionList : undefined,
      });
      onAdded(`${noun[0].toUpperCase()}${noun.slice(1)} "${displayName}" added to ${org.name}.`);
    } catch (err: any) {
      setError(err.message ?? `Failed to add ${noun}.`);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="fixed inset-0 bg-black/50 flex items-center justify-center z-50 p-4">
      <div className="bg-white rounded-lg shadow-xl max-w-lg w-full max-h-[90vh] overflow-y-auto p-6">
        <div className="flex items-center justify-between mb-1">
          <h2 className="text-lg font-semibold text-gray-900">Add {isAws ? 'AWS account' : 'GCP project'}</h2>
          <button onClick={onClose} className="text-gray-400 hover:text-gray-600">
            <svg className="h-5 w-5" fill="none" viewBox="0 0 24 24" stroke="currentColor"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" /></svg>
          </button>
        </div>
        <p className="text-xs text-gray-500 mb-4">Under organization <strong>{org.name}</strong></p>

        {error && <div className="mb-4 p-3 bg-red-50 border border-red-200 rounded-lg text-sm text-red-700">{error}</div>}

        <form onSubmit={submit} className="space-y-4">
          <Field label={isAws ? 'AWS Account ID' : 'GCP Project ID'} required mono value={externalId} onChange={setExternalId}
            placeholder={isAws ? '123456789012' : 'my-gcp-project'} />
          <Field label="Display Name" required value={displayName} onChange={setDisplayName}
            placeholder={isAws ? 'Production account' : 'Production project'} />

          {isAws ? (
            <div className="space-y-3 p-4 bg-gray-50 rounded-lg border border-gray-200">
              <p className="text-xs text-gray-500">IAM access keys (read + write for enabled playbooks). Stored only in the secret store.</p>
              <Field label="Access Key ID" mono value={accessKeyId} onChange={setAccessKeyId} placeholder="AKIA..." />
              <Field label="Secret Access Key" type="password" mono value={secretAccessKey} onChange={setSecretAccessKey} placeholder="Secret access key" />
              <Field label="Session Token (optional)" type="password" mono value={sessionToken} onChange={setSessionToken} placeholder="For temporary STS credentials" />
              <Field label="Default Region" mono value={defaultRegion} onChange={setDefaultRegion} placeholder="us-east-1" />
            </div>
          ) : (
            <div className="space-y-3 p-4 bg-gray-50 rounded-lg border border-gray-200">
              <p className="text-xs text-gray-500">Service account key JSON. Stored only in the secret store.</p>
              <textarea value={serviceAccountJson} onChange={e => setServiceAccountJson(e.target.value)} rows={5}
                className="w-full px-3 py-2 border border-gray-300 rounded-lg text-xs font-mono focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                placeholder={'{\n  "type": "service_account",\n  "client_email": "...",\n  "private_key": "..."\n}'} />
            </div>
          )}

          <Field label="Regions to scan" mono value={regions} onChange={setRegions}
            placeholder={isAws ? 'us-east-1, us-west-2' : 'Leave blank for project-wide'} />

          <div className="flex justify-end gap-3 pt-4 border-t border-gray-200">
            <button type="button" onClick={onClose} className="px-4 py-2 text-gray-700 border border-gray-300 rounded-lg text-sm hover:bg-gray-50">Cancel</button>
            <button type="submit" disabled={busy} className="px-4 py-2 bg-blue-600 text-white rounded-lg text-sm hover:bg-blue-700 disabled:opacity-50 flex items-center gap-2">
              {busy && <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white" />}
              {busy ? 'Adding...' : `Add ${noun}`}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}

// Small labeled input helper
function Field({ label, value, onChange, placeholder, required, mono, type = 'text', pattern }: {
  label: string; value: string; onChange: (v: string) => void; placeholder?: string;
  required?: boolean; mono?: boolean; type?: string; pattern?: string;
}) {
  return (
    <div>
      <label className="block text-sm font-medium text-gray-700 mb-1">
        {label}{required && <span className="text-red-500"> *</span>}
      </label>
      <input
        type={type} required={required} pattern={pattern} value={value}
        onChange={e => onChange(e.target.value)} placeholder={placeholder}
        className={`w-full px-3 py-2 border border-gray-300 rounded-lg text-sm focus:ring-2 focus:ring-blue-500 focus:border-blue-500 ${mono ? 'font-mono' : ''}`}
      />
    </div>
  );
}
