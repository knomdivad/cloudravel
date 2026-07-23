'use client';

import { useState, useEffect, useCallback } from 'react';
import { api } from '@/lib/api';
import { useTenantContext } from '@/contexts/TenantContext';
import type { CloudAccount } from '@/lib/types';

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
type Provider = 'azure' | 'aws' | 'gcp';

const PROVIDER_BADGES: Record<string, string> = {
  azure: 'bg-blue-100 text-blue-700',
  aws: 'bg-amber-100 text-amber-800',
  gcp: 'bg-emerald-100 text-emerald-700',
};

const INITIAL_FORM = {
  provider: 'azure' as Provider,
  // Azure tenant onboarding
  displayName: '',
  azureTenantId: '',
  onboardingMethod: 'lighthouse' as 'lighthouse' | 'app_registration',
  clientId: '',
  clientSecret: '',
  lighthouseDelegationId: '',
  subscriptionIds: '',
  // AWS / GCP cloud account
  externalId: '',
  awsAccessKeyId: '',
  awsSecretAccessKey: '',
  awsSessionToken: '',
  awsDefaultRegion: 'us-east-1',
  gcpServiceAccountJson: '',
  regions: '',
};

export default function CloudsPage() {
  const { tenantId, currentTenant } = useTenantContext();

  const [tenants, setTenants] = useState<TenantSummary[]>([]);
  const [cloudAccounts, setCloudAccounts] = useState<CloudAccount[]>([]);
  const [loading, setLoading] = useState(true);
  const [showAdd, setShowAdd] = useState(false);
  const [formData, setFormData] = useState(INITIAL_FORM);
  const [submitting, setSubmitting] = useState(false);
  const [formError, setFormError] = useState<string | null>(null);
  const [toast, setToast] = useState<{ message: string; type: ToastType } | null>(null);
  const [statusAction, setStatusAction] = useState<{ tenant: TenantSummary; newStatus: string } | null>(null);
  const [updatingStatus, setUpdatingStatus] = useState(false);
  const [collectingTenant, setCollectingTenant] = useState<string | null>(null);

  const showToast = useCallback((message: string, type: ToastType) => {
    setToast({ message, type });
    setTimeout(() => setToast(null), 5000);
  }, []);

  const fetchTenants = useCallback(async () => {
    try {
      const data = await api.getTenants();
      setTenants(data || []);
    } catch (err) {
      console.error('Failed to load tenants', err);
      showToast('Failed to load clouds.', 'error');
    } finally {
      setLoading(false);
    }
  }, [showToast]);

  const fetchCloudAccounts = useCallback(async () => {
    if (!tenantId) {
      setCloudAccounts([]);
      return;
    }
    try {
      const data = await api.getCloudAccounts(tenantId);
      setCloudAccounts(data.accounts || []);
    } catch (err) {
      console.error('Failed to load cloud accounts', err);
    }
  }, [tenantId]);

  useEffect(() => { fetchTenants(); }, [fetchTenants]);
  useEffect(() => { fetchCloudAccounts(); }, [fetchCloudAccounts]);

  const openAdd = () => {
    setFormData(INITIAL_FORM);
    setFormError(null);
    setShowAdd(true);
  };

  const setProvider = (provider: Provider) =>
    setFormData(d => ({ ...d, provider }));

  const handleAdd = async (e: React.FormEvent) => {
    e.preventDefault();
    setFormError(null);
    setSubmitting(true);

    try {
      if (formData.provider === 'azure') {
        const isAppReg = formData.onboardingMethod === 'app_registration';
        const subIds = formData.subscriptionIds
          .split(/[,\n]+/)
          .map(s => s.trim())
          .filter(Boolean);

        await api.onboardTenant({
          displayName: formData.displayName,
          azureTenantId: formData.azureTenantId,
          onboardingMethod: formData.onboardingMethod,
          clientId: isAppReg ? formData.clientId : undefined,
          clientSecret: isAppReg ? formData.clientSecret || undefined : undefined,
          lighthouseDelegationId: !isAppReg ? formData.lighthouseDelegationId || undefined : undefined,
          subscriptionIds: subIds.length > 0 ? subIds : undefined,
        });
        showToast(`Azure tenant "${formData.displayName}" onboarded.`, 'success');
        fetchTenants();
      } else {
        // AWS / GCP attach to the currently selected cloud (tenant).
        if (!tenantId) {
          setFormError('Select or onboard an Azure tenant first — AWS/GCP accounts attach to a cloud context.');
          setSubmitting(false);
          return;
        }

        let credentialJson: string | undefined;
        if (formData.provider === 'aws') {
          if (formData.awsAccessKeyId && formData.awsSecretAccessKey) {
            credentialJson = JSON.stringify({
              accessKeyId: formData.awsAccessKeyId.trim(),
              secretAccessKey: formData.awsSecretAccessKey.trim(),
              sessionToken: formData.awsSessionToken.trim() || undefined,
              defaultRegion: formData.awsDefaultRegion.trim() || 'us-east-1',
            });
          }
        } else {
          if (formData.gcpServiceAccountJson.trim()) {
            JSON.parse(formData.gcpServiceAccountJson); // validate before send
            credentialJson = formData.gcpServiceAccountJson.trim();
          }
        }

        const regionList = formData.regions
          .split(/[,\n]+/)
          .map(r => r.trim())
          .filter(Boolean);

        await api.linkCloudAccount(tenantId, {
          provider: formData.provider,
          externalId: formData.externalId.trim(),
          displayName: formData.displayName.trim(),
          credentialJson,
          regions: regionList.length > 0 ? regionList : undefined,
        });
        showToast(`${formData.provider.toUpperCase()} account "${formData.displayName}" linked.`, 'success');
        fetchCloudAccounts();
      }
      setShowAdd(false);
    } catch (err) {
      const msg = err instanceof Error ? err.message : 'Failed to add cloud.';
      setFormError(formData.provider === 'gcp' && msg.includes('JSON')
        ? 'The GCP service account key must be valid JSON.'
        : msg);
    } finally {
      setSubmitting(false);
    }
  };

  const handleStatusUpdate = async () => {
    if (!statusAction) return;
    setUpdatingStatus(true);
    try {
      await api.updateTenantStatus(statusAction.tenant.tenantId, statusAction.newStatus);
      showToast(
        `${statusAction.tenant.displayName} status changed to ${statusAction.newStatus}.`,
        'success'
      );
      setStatusAction(null);
      fetchTenants();
    } catch (err) {
      const msg = err instanceof Error ? err.message : 'Failed to update status.';
      showToast(msg, 'error');
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
      const msg = err instanceof Error ? err.message : 'Failed to collect inventory.';
      showToast(msg, 'error');
    } finally {
      setCollectingTenant(null);
    }
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

  const getStatusActions = (status: string): { label: string; value: string; destructive?: boolean }[] => {
    switch (status) {
      case 'active':
        return [
          { label: 'Suspend', value: 'suspended', destructive: true },
          { label: 'Offboard', value: 'offboarded', destructive: true },
        ];
      case 'degraded':
        return [
          { label: 'Reactivate', value: 'active' },
          { label: 'Suspend', value: 'suspended', destructive: true },
        ];
      case 'suspended':
        return [
          { label: 'Reactivate', value: 'active' },
          { label: 'Offboard', value: 'offboarded', destructive: true },
        ];
      default:
        return [];
    }
  };

  if (loading) {
    return (
      <div className="flex items-center justify-center h-64">
        <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-blue-600" />
      </div>
    );
  }

  const totalClouds = tenants.length + cloudAccounts.length;

  return (
    <div className="space-y-6">
      {toast && (
        <div className={`fixed top-4 right-4 z-[60] max-w-sm px-4 py-3 rounded-lg shadow-lg text-sm font-medium flex items-center gap-2 ${
          toast.type === 'success'
            ? 'bg-green-50 border border-green-200 text-green-800'
            : 'bg-red-50 border border-red-200 text-red-800'
        }`}>
          <span>{toast.type === 'success' ? '✓' : '✕'}</span>
          <span className="flex-1">{toast.message}</span>
          <button onClick={() => setToast(null)} className="ml-2 opacity-60 hover:opacity-100">✕</button>
        </div>
      )}

      {/* Page Header */}
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-bold text-gray-900">Clouds</h1>
          <p className="text-sm text-gray-500 mt-1">
            Azure tenants, AWS accounts, and GCP projects &middot; {totalClouds} connected
          </p>
        </div>
        <button
          onClick={openAdd}
          className="px-4 py-2 bg-blue-600 text-white rounded-lg hover:bg-blue-700 text-sm font-medium"
        >
          + Add Cloud
        </button>
      </div>

      {/* Add Cloud Modal */}
      {showAdd && (
        <div className="fixed inset-0 bg-black/50 flex items-center justify-center z-50 p-4">
          <div className="bg-white rounded-lg shadow-xl max-w-lg w-full max-h-[90vh] overflow-y-auto p-6">
            <div className="flex items-center justify-between mb-4">
              <h2 className="text-lg font-semibold text-gray-900">Add Cloud</h2>
              <button onClick={() => setShowAdd(false)} className="text-gray-400 hover:text-gray-600">
                <svg className="h-5 w-5" fill="none" viewBox="0 0 24 24" stroke="currentColor"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" /></svg>
              </button>
            </div>

            {/* Provider selector */}
            <div className="mb-4">
              <label className="block text-sm font-medium text-gray-700 mb-1">Provider</label>
              <div className="grid grid-cols-3 gap-2">
                {(['azure', 'aws', 'gcp'] as Provider[]).map(p => (
                  <button
                    key={p}
                    type="button"
                    onClick={() => setProvider(p)}
                    className={`px-3 py-2 rounded-lg border text-sm font-medium text-center transition-colors ${
                      formData.provider === p
                        ? 'border-blue-500 bg-blue-50 text-blue-700'
                        : 'border-gray-300 text-gray-600 hover:bg-gray-50'
                    }`}
                  >
                    {p === 'azure' ? 'Azure' : p === 'aws' ? 'AWS' : 'Google Cloud'}
                  </button>
                ))}
              </div>
            </div>

            {formError && (
              <div className="mb-4 p-3 bg-red-50 border border-red-200 rounded-lg text-sm text-red-700">
                {formError}
              </div>
            )}

            <form onSubmit={handleAdd} className="space-y-4">
              {/* Display Name (all providers) */}
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">
                  Display Name <span className="text-red-500">*</span>
                </label>
                <input
                  type="text"
                  required
                  value={formData.displayName}
                  onChange={e => setFormData(d => ({ ...d, displayName: e.target.value }))}
                  className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                  placeholder={formData.provider === 'azure' ? 'Contoso Corp' : formData.provider === 'aws' ? 'Production AWS' : 'Production GCP'}
                />
              </div>

              {/* ---------- Azure ---------- */}
              {formData.provider === 'azure' && (
                <>
                  <div>
                    <label className="block text-sm font-medium text-gray-700 mb-1">
                      Azure Tenant ID <span className="text-red-500">*</span>
                    </label>
                    <input
                      type="text"
                      required
                      pattern="[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}"
                      title="Must be a valid GUID (e.g. 00000000-0000-0000-0000-000000000000)"
                      value={formData.azureTenantId}
                      onChange={e => setFormData(d => ({ ...d, azureTenantId: e.target.value }))}
                      className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm font-mono focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                      placeholder="00000000-0000-0000-0000-000000000000"
                    />
                  </div>

                  <div>
                    <label className="block text-sm font-medium text-gray-700 mb-1">Onboarding Method</label>
                    <div className="grid grid-cols-2 gap-2">
                      <button
                        type="button"
                        onClick={() => setFormData(d => ({ ...d, onboardingMethod: 'lighthouse' }))}
                        className={`px-3 py-2 rounded-lg border text-sm font-medium text-center transition-colors ${
                          formData.onboardingMethod === 'lighthouse'
                            ? 'border-blue-500 bg-blue-50 text-blue-700'
                            : 'border-gray-300 text-gray-600 hover:bg-gray-50'
                        }`}
                      >
                        Azure Lighthouse
                      </button>
                      <button
                        type="button"
                        onClick={() => setFormData(d => ({ ...d, onboardingMethod: 'app_registration' }))}
                        className={`px-3 py-2 rounded-lg border text-sm font-medium text-center transition-colors ${
                          formData.onboardingMethod === 'app_registration'
                            ? 'border-blue-500 bg-blue-50 text-blue-700'
                            : 'border-gray-300 text-gray-600 hover:bg-gray-50'
                        }`}
                      >
                        App Registration
                      </button>
                    </div>
                  </div>

                  {formData.onboardingMethod === 'lighthouse' && (
                    <div className="space-y-4 p-4 bg-gray-50 rounded-lg border border-gray-200">
                      <p className="text-xs text-gray-500">
                        Azure Lighthouse provides cross-tenant access without storing credentials.
                        Deploy the delegation template in the customer&apos;s tenant first.
                      </p>
                      <div>
                        <label className="block text-sm font-medium text-gray-700 mb-1">Lighthouse Delegation ID</label>
                        <input
                          type="text"
                          value={formData.lighthouseDelegationId}
                          onChange={e => setFormData(d => ({ ...d, lighthouseDelegationId: e.target.value }))}
                          className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm font-mono focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                          placeholder="Optional — auto-discovered if omitted"
                        />
                      </div>
                    </div>
                  )}

                  {formData.onboardingMethod === 'app_registration' && (
                    <div className="space-y-4 p-4 bg-gray-50 rounded-lg border border-gray-200">
                      <p className="text-xs text-gray-500">
                        Create an app registration in the customer&apos;s Entra ID with
                        <strong> Reader</strong> and <strong>Security Reader</strong> roles on target subscriptions.
                      </p>
                      <div>
                        <label className="block text-sm font-medium text-gray-700 mb-1">
                          Client ID <span className="text-red-500">*</span>
                        </label>
                        <input
                          type="text"
                          required
                          pattern="[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}"
                          title="Must be a valid GUID"
                          value={formData.clientId}
                          onChange={e => setFormData(d => ({ ...d, clientId: e.target.value }))}
                          className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm font-mono focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                          placeholder="Application (client) ID"
                        />
                      </div>
                      <div>
                        <label className="block text-sm font-medium text-gray-700 mb-1">Client Secret</label>
                        <input
                          type="password"
                          value={formData.clientSecret}
                          onChange={e => setFormData(d => ({ ...d, clientSecret: e.target.value }))}
                          className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm font-mono focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                          placeholder="Client secret value"
                        />
                        <p className="text-xs text-gray-400 mt-1">
                          Stored securely in the platform&apos;s secret store. Leave blank to add later.
                        </p>
                      </div>
                    </div>
                  )}

                  <div>
                    <label className="block text-sm font-medium text-gray-700 mb-1">Subscription IDs</label>
                    <textarea
                      value={formData.subscriptionIds}
                      onChange={e => setFormData(d => ({ ...d, subscriptionIds: e.target.value }))}
                      className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm font-mono focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                      rows={2}
                      placeholder={"One per line or comma-separated"}
                    />
                    <p className="text-xs text-gray-400 mt-1">
                      Optional — leave blank to discover automatically.
                    </p>
                  </div>
                </>
              )}

              {/* ---------- AWS / GCP: attach-to note ---------- */}
              {formData.provider !== 'azure' && (
                <div className="p-3 bg-blue-50 border border-blue-200 rounded-lg text-xs text-blue-800">
                  {tenantId
                    ? <>Attaching to cloud context: <strong>{currentTenant?.displayName}</strong>. Switch the cloud in the sidebar to attach elsewhere.</>
                    : <>No cloud context selected. Onboard or select an Azure tenant first — AWS/GCP accounts attach to one.</>}
                </div>
              )}

              {/* ---------- AWS ---------- */}
              {formData.provider === 'aws' && (
                <>
                  <div>
                    <label className="block text-sm font-medium text-gray-700 mb-1">
                      AWS Account ID <span className="text-red-500">*</span>
                    </label>
                    <input
                      type="text"
                      required
                      value={formData.externalId}
                      onChange={e => setFormData(d => ({ ...d, externalId: e.target.value }))}
                      className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm font-mono focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                      placeholder="123456789012"
                    />
                  </div>
                  <div className="space-y-3 p-4 bg-gray-50 rounded-lg border border-gray-200">
                    <p className="text-xs text-gray-500">
                      IAM access keys with <code>tag:GetResources</code> (read) plus the write permissions
                      for any playbooks you enable. Stored only in the secret store.
                    </p>
                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-1">Access Key ID</label>
                      <input type="text" value={formData.awsAccessKeyId}
                        onChange={e => setFormData(d => ({ ...d, awsAccessKeyId: e.target.value }))}
                        className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm font-mono focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                        placeholder="AKIA..." />
                    </div>
                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-1">Secret Access Key</label>
                      <input type="password" value={formData.awsSecretAccessKey}
                        onChange={e => setFormData(d => ({ ...d, awsSecretAccessKey: e.target.value }))}
                        className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm font-mono focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                        placeholder="Secret access key" />
                    </div>
                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-1">Session Token (optional)</label>
                      <input type="password" value={formData.awsSessionToken}
                        onChange={e => setFormData(d => ({ ...d, awsSessionToken: e.target.value }))}
                        className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm font-mono focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                        placeholder="For temporary STS credentials" />
                    </div>
                    <div>
                      <label className="block text-sm font-medium text-gray-700 mb-1">Default Region</label>
                      <input type="text" value={formData.awsDefaultRegion}
                        onChange={e => setFormData(d => ({ ...d, awsDefaultRegion: e.target.value }))}
                        className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm font-mono focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                        placeholder="us-east-1" />
                    </div>
                  </div>
                  <div>
                    <label className="block text-sm font-medium text-gray-700 mb-1">Regions to scan</label>
                    <input type="text" value={formData.regions}
                      onChange={e => setFormData(d => ({ ...d, regions: e.target.value }))}
                      className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm font-mono focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                      placeholder="us-east-1, us-west-2 (defaults to the default region)" />
                  </div>
                </>
              )}

              {/* ---------- GCP ---------- */}
              {formData.provider === 'gcp' && (
                <>
                  <div>
                    <label className="block text-sm font-medium text-gray-700 mb-1">
                      GCP Project ID <span className="text-red-500">*</span>
                    </label>
                    <input
                      type="text"
                      required
                      value={formData.externalId}
                      onChange={e => setFormData(d => ({ ...d, externalId: e.target.value }))}
                      className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm font-mono focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                      placeholder="my-gcp-project"
                    />
                  </div>
                  <div className="space-y-3 p-4 bg-gray-50 rounded-lg border border-gray-200">
                    <p className="text-xs text-gray-500">
                      Service account key JSON (<code>roles/cloudasset.viewer</code> plus write roles for
                      enabled playbooks). Stored only in the secret store.
                    </p>
                    <textarea
                      value={formData.gcpServiceAccountJson}
                      onChange={e => setFormData(d => ({ ...d, gcpServiceAccountJson: e.target.value }))}
                      rows={5}
                      className="w-full px-3 py-2 border border-gray-300 rounded-lg text-xs font-mono focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                      placeholder={'{\n  "type": "service_account",\n  "client_email": "...",\n  "private_key": "..."\n}'}
                    />
                  </div>
                </>
              )}

              {/* Actions */}
              <div className="flex justify-end gap-3 pt-4 border-t border-gray-200">
                <button
                  type="button"
                  onClick={() => setShowAdd(false)}
                  className="px-4 py-2 text-gray-700 border border-gray-300 rounded-lg text-sm hover:bg-gray-50"
                >
                  Cancel
                </button>
                <button
                  type="submit"
                  disabled={submitting}
                  className="px-4 py-2 bg-blue-600 text-white rounded-lg text-sm hover:bg-blue-700 disabled:opacity-50 flex items-center gap-2"
                >
                  {submitting && <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white" />}
                  {submitting
                    ? (formData.provider === 'azure' ? 'Onboarding...' : 'Linking...')
                    : (formData.provider === 'azure' ? 'Onboard Tenant' : 'Link Account')}
                </button>
              </div>
            </form>
          </div>
        </div>
      )}

      {/* Status Change Confirmation Modal */}
      {statusAction && (
        <div className="fixed inset-0 bg-black/50 flex items-center justify-center z-50 p-4">
          <div className="bg-white rounded-lg shadow-xl max-w-md w-full p-6">
            <h3 className="text-lg font-semibold text-gray-900 mb-2">
              {statusAction.newStatus === 'Active' ? 'Reactivate' : statusAction.newStatus} Tenant
            </h3>
            <p className="text-sm text-gray-600 mb-1">
              Are you sure you want to change <strong>{statusAction.tenant.displayName}</strong> to <strong>{statusAction.newStatus}</strong>?
            </p>
            {statusAction.newStatus === 'Offboarded' && (
              <p className="text-sm text-amber-600 mt-2">
                Offboarding will stop all monitoring and data collection for this tenant.
              </p>
            )}
            {statusAction.newStatus === 'Suspended' && (
              <p className="text-sm text-amber-600 mt-2">
                Suspending will pause all monitoring and change polling for this tenant.
              </p>
            )}
            <div className="flex justify-end gap-3 mt-6">
              <button
                onClick={() => setStatusAction(null)}
                className="px-4 py-2 text-gray-700 border border-gray-300 rounded-lg text-sm hover:bg-gray-50"
              >
                Cancel
              </button>
              <button
                onClick={handleStatusUpdate}
                disabled={updatingStatus}
                className={`px-4 py-2 rounded-lg text-sm text-white flex items-center gap-2 disabled:opacity-50 ${
                  statusAction.newStatus === 'Active'
                    ? 'bg-green-600 hover:bg-green-700'
                    : 'bg-red-600 hover:bg-red-700'
                }`}
              >
                {updatingStatus && <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white" />}
                {updatingStatus ? 'Updating...' : 'Confirm'}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Azure tenants */}
      {tenants.length > 0 && (
        <div>
          <h2 className="text-sm font-semibold text-gray-700 uppercase tracking-wide mb-3">Azure Tenants</h2>
          <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
            {tenants.map(tenant => {
              const actions = getStatusActions(tenant.status);
              return (
                <div
                  key={tenant.tenantId}
                  className="bg-white rounded-lg border border-gray-200 p-5 hover:shadow-md transition-shadow"
                >
                  <div className="flex items-start justify-between mb-3">
                    <div className="flex items-center gap-2 min-w-0">
                      <span className={`text-xs px-2 py-0.5 rounded-full font-medium ${PROVIDER_BADGES.azure}`}>Azure</span>
                      <h3 className="font-semibold text-gray-900 truncate">{tenant.displayName}</h3>
                    </div>
                    <span className={`px-2 py-0.5 rounded-full text-xs font-medium whitespace-nowrap ${statusBadge(tenant.status)}`}>
                      {tenant.status}
                    </span>
                  </div>
                  <p className="text-xs text-gray-500 font-mono mb-4 truncate">{tenant.azureTenantId}</p>
                  <div className="grid grid-cols-3 gap-2 text-center mb-4">
                    <div>
                      <p className="text-lg font-bold text-gray-900">{tenant.resourceCount ?? '—'}</p>
                      <p className="text-xs text-gray-500">Resources</p>
                    </div>
                    <div>
                      <p className="text-lg font-bold text-gray-900">{tenant.changes24H ?? '—'}</p>
                      <p className="text-xs text-gray-500">Changes 24h</p>
                    </div>
                    <div>
                      <p className="text-xs text-gray-500 mt-1">
                        {tenant.lastSnapshotAt
                          ? new Date(tenant.lastSnapshotAt).toLocaleDateString()
                          : 'No snapshot'}
                      </p>
                      <p className="text-xs text-gray-500">Last Snapshot</p>
                    </div>
                  </div>

                  {actions.length > 0 && (
                    <div className="flex gap-2 pt-3 border-t border-gray-100">
                      {actions.map(action => (
                        <button
                          key={action.value}
                          onClick={() => setStatusAction({ tenant, newStatus: action.value })}
                          className={`flex-1 px-3 py-1.5 rounded text-xs font-medium transition-colors ${
                            action.destructive
                              ? 'text-red-600 border border-red-200 hover:bg-red-50'
                              : 'text-green-600 border border-green-200 hover:bg-green-50'
                          }`}
                        >
                          {action.label}
                        </button>
                      ))}
                    </div>
                  )}

                  {tenant.status === 'active' && (
                    <div className="pt-2">
                      <button
                        onClick={() => handleCollectInventory(tenant)}
                        disabled={collectingTenant === tenant.tenantId}
                        className="w-full px-3 py-1.5 rounded text-xs font-medium transition-colors text-blue-600 border border-blue-200 hover:bg-blue-50 disabled:opacity-50 flex items-center justify-center gap-2"
                      >
                        {collectingTenant === tenant.tenantId && (
                          <div className="animate-spin rounded-full h-3 w-3 border-b-2 border-blue-600" />
                        )}
                        {collectingTenant === tenant.tenantId ? 'Collecting...' : 'Collect Inventory'}
                      </button>
                    </div>
                  )}
                </div>
              );
            })}
          </div>
        </div>
      )}

      {/* Linked AWS / GCP accounts */}
      {cloudAccounts.length > 0 && (
        <div>
          <h2 className="text-sm font-semibold text-gray-700 uppercase tracking-wide mb-3">
            AWS &amp; GCP Accounts{currentTenant ? ` — ${currentTenant.displayName}` : ''}
          </h2>
          <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
            {cloudAccounts.map(acct => (
              <div key={acct.accountId} className="bg-white rounded-lg border border-gray-200 p-5">
                <div className="flex items-start justify-between mb-3">
                  <div className="flex items-center gap-2 min-w-0">
                    <span className={`text-xs px-2 py-0.5 rounded-full font-medium ${PROVIDER_BADGES[acct.provider.toLowerCase()] ?? ''}`}>
                      {acct.provider.toUpperCase()}
                    </span>
                    <h3 className="font-semibold text-gray-900 truncate">{acct.displayName}</h3>
                  </div>
                  <span className={`px-2 py-0.5 rounded-full text-xs font-medium whitespace-nowrap ${statusBadge(acct.status)}`}>
                    {acct.status}
                  </span>
                </div>
                <p className="text-xs text-gray-500 font-mono mb-3 truncate">{acct.externalId}</p>
                {acct.regions && acct.regions.length > 0 && (
                  <p className="text-xs text-gray-500 mb-2">Regions: {acct.regions.join(', ')}</p>
                )}
                <p className="text-xs text-gray-500">
                  Last inventory: {acct.lastInventoryAt ? new Date(acct.lastInventoryAt).toLocaleString() : 'never'}
                </p>
                {acct.lastError && (
                  <p className="text-xs text-red-600 mt-2 break-words">{acct.lastError}</p>
                )}
              </div>
            ))}
          </div>
        </div>
      )}

      {/* Empty state */}
      {totalClouds === 0 && (
        <div className="text-center py-12 bg-white rounded-lg border border-gray-200">
          <div className="mx-auto mb-4">
            <svg className="mx-auto h-12 w-12 text-gray-300" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={1.5} d="M2.25 15a4.5 4.5 0 0 0 4.5 4.5H18a3.75 3.75 0 0 0 1.332-7.257 3 3 0 0 0-3.758-3.848 5.25 5.25 0 0 0-10.233 2.33A4.502 4.502 0 0 0 2.25 15Z" />
            </svg>
          </div>
          <p className="text-gray-500 mb-1">No clouds connected yet</p>
          <p className="text-sm text-gray-400 mb-4">
            Connect an Azure tenant, AWS account, or GCP project to start monitoring.
          </p>
          <button
            onClick={openAdd}
            className="px-4 py-2 bg-blue-600 text-white rounded-lg hover:bg-blue-700 text-sm"
          >
            Add First Cloud
          </button>
        </div>
      )}
    </div>
  );
}
