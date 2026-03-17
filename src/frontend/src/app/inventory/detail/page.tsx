'use client';

import { useState, useEffect, Suspense } from 'react';
import { useSearchParams, useRouter } from 'next/navigation';
import { useTenant } from '@/contexts/TenantContext';
import { api } from '@/lib/api';

interface ResourceDetail {
  resourceId: string;
  subscriptionId: string;
  resourceGroup: string;
  resourceType: string;
  resourceName: string;
  location: string;
  skuName?: string;
  skuTier?: string;
  skuCapacity?: number;
  tags?: Record<string, string>;
  identityType?: string;
  identityPrincipalIds?: string;
  propertiesJson?: string;
  networkingJson?: string;
  securityConfigJson?: string;
}

interface ResourceChange {
  changeId: string;
  resourceId: string;
  resourceType: string;
  changeType: string;
  detectedAt: string;
  changedProperties?: { property: string; oldValue?: string; newValue?: string }[];
  actorName?: string;
  clientType?: string;
  classification: string;
  severity?: string;
}

function ResourceDetailContent() {
  const searchParams = useSearchParams();
  const router = useRouter();
  const { selectedTenant } = useTenant();
  const [resource, setResource] = useState<ResourceDetail | null>(null);
  const [changes, setChanges] = useState<ResourceChange[]>([]);
  const [loading, setLoading] = useState(true);
  const [activeTab, setActiveTab] = useState<'overview' | 'properties' | 'networking' | 'security' | 'changes'>('overview');

  const resourceId = searchParams.get('id') || '';

  useEffect(() => {
    if (!selectedTenant || !resourceId) return;
    const fetchData = async () => {
      setLoading(true);
      try {
        const [resourceData, changesData] = await Promise.all([
          api.getResourceDetail(selectedTenant, resourceId),
          api.getChanges(selectedTenant, { resourceId, limit: 50 }),
        ]);
        setResource(resourceData);
        setChanges(changesData.changes || []);
      } catch (err) {
        console.error('Failed to load resource', err);
      } finally {
        setLoading(false);
      }
    };
    fetchData();
  }, [selectedTenant, resourceId]);

  if (!resourceId) {
    return (
      <div className="bg-red-50 border border-red-200 rounded-lg p-6 text-center">
        <h2 className="text-lg font-semibold text-red-800">No Resource Selected</h2>
        <p className="text-red-600 mt-2">Please select a resource from the inventory list.</p>
        <button onClick={() => router.push('/inventory')} className="mt-4 px-4 py-2 bg-red-600 text-white rounded-lg hover:bg-red-700">
          Go to Inventory
        </button>
      </div>
    );
  }

  if (loading) {
    return (
      <div className="flex items-center justify-center h-64">
        <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-blue-600" />
      </div>
    );
  }

  if (!resource) {
    return (
      <div className="bg-red-50 border border-red-200 rounded-lg p-6 text-center">
        <h2 className="text-lg font-semibold text-red-800">Resource Not Found</h2>
        <p className="text-red-600 mt-2">The resource could not be found in the current inventory snapshot.</p>
        <button onClick={() => router.back()} className="mt-4 px-4 py-2 bg-red-600 text-white rounded-lg hover:bg-red-700">
          Go Back
        </button>
      </div>
    );
  }

  const parseJson = (json?: string) => {
    if (!json) return null;
    try { return JSON.parse(json); } catch { return null; }
  };

  const properties = parseJson(resource.propertiesJson);
  const networking = parseJson(resource.networkingJson);
  const security = parseJson(resource.securityConfigJson);
  const isSubscription = resource.resourceType === 'microsoft.resources/subscriptions';

  const tabs = [
    { id: 'overview' as const, label: 'Overview' },
    { id: 'properties' as const, label: 'Properties', disabled: !properties },
    { id: 'networking' as const, label: 'Networking', disabled: !networking || isSubscription },
    { id: 'security' as const, label: 'Security Config', disabled: !security && !isSubscription },
    { id: 'changes' as const, label: `Changes (${changes.length})` },
  ];

  return (
    <div className="space-y-6">
      {/* Header */}
      <div className="flex items-start justify-between">
        <div>
          <button onClick={() => router.back()} className="text-sm text-blue-600 hover:text-blue-800 mb-2 flex items-center gap-1">
            ← Back to Inventory
          </button>
          <h1 className="text-2xl font-bold text-gray-900">{resource.resourceName}</h1>
          <p className="text-sm text-gray-500 mt-1 font-mono break-all">{resource.resourceId}</p>
        </div>
        <span className="px-3 py-1 bg-blue-100 text-blue-800 text-sm rounded-full">
          {resource.resourceType === 'microsoft.resources/subscriptions' ? 'Subscription' : resource.resourceType}
        </span>
      </div>

      {/* Tabs */}
      <div className="border-b border-gray-200">
        <nav className="flex space-x-8">
          {tabs.map(tab => (
            <button
              key={tab.id}
              onClick={() => !tab.disabled && setActiveTab(tab.id)}
              className={`py-2 px-1 border-b-2 text-sm font-medium ${
                activeTab === tab.id
                  ? 'border-blue-500 text-blue-600'
                  : tab.disabled
                    ? 'border-transparent text-gray-300 cursor-not-allowed'
                    : 'border-transparent text-gray-500 hover:text-gray-700 hover:border-gray-300'
              }`}
            >
              {tab.label}
            </button>
          ))}
        </nav>
      </div>

      {/* Tab Content */}
      {activeTab === 'overview' && isSubscription && (
        <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
          <div className="bg-white rounded-lg border border-gray-200 p-6">
            <h3 className="text-sm font-semibold text-gray-500 uppercase mb-4">Subscription Information</h3>
            <dl className="space-y-3">
              <DetailRow label="Name" value={resource.resourceName} />
              <DetailRow label="Subscription ID" value={resource.subscriptionId} />
              <DetailRow label="State" value={properties?.state || '—'} />
              <DetailRow label="Home Tenant ID" value={properties?.homeTenantId || '—'} />
              <DetailRow label="Authorization Source" value={properties?.authorizationSource || '—'} />
            </dl>
          </div>
          <div className="bg-white rounded-lg border border-gray-200 p-6">
            <h3 className="text-sm font-semibold text-gray-500 uppercase mb-4">Management & Security</h3>
            <dl className="space-y-3">
              <DetailRow label="Management Group" value={properties?.managementGroupName || '—'} />
              <DetailRow label="Management Group ID" value={properties?.managementGroupId || '—'} />
              {properties?.secureScorePercentage != null ? (
                <>
                  <div className="flex justify-between items-center">
                    <dt className="text-sm text-gray-500">Secure Score</dt>
                    <dd className="text-sm font-medium text-gray-900">
                      {Math.round(properties.secureScoreCurrent ?? 0)} / {Math.round(properties.secureScoreMax ?? 0)}
                    </dd>
                  </div>
                  <div>
                    <div className="flex justify-between text-xs text-gray-500 mb-1">
                      <span>Score Percentage</span>
                      <span>{Math.round(properties.secureScorePercentage * 100)}%</span>
                    </div>
                    <div className="w-full bg-gray-200 rounded-full h-2">
                      <div
                        className={`h-2 rounded-full ${
                          properties.secureScorePercentage >= 0.7 ? 'bg-green-500' :
                          properties.secureScorePercentage >= 0.4 ? 'bg-yellow-500' : 'bg-red-500'
                        }`}
                        style={{ width: `${Math.round(properties.secureScorePercentage * 100)}%` }}
                      />
                    </div>
                  </div>
                </>
              ) : (
                <DetailRow label="Secure Score" value="Not available" />
              )}
            </dl>
          </div>
          {resource.tags && Object.keys(resource.tags).length > 0 && (
            <div className="bg-white rounded-lg border border-gray-200 p-6 md:col-span-2">
              <h3 className="text-sm font-semibold text-gray-500 uppercase mb-4">Tags</h3>
              <div className="flex flex-wrap gap-2">
                {Object.entries(resource.tags).map(([key, value]) => (
                  <span key={key} className="inline-flex items-center px-3 py-1 rounded-full text-xs font-medium bg-gray-100 text-gray-800">
                    {key}: {value}
                  </span>
                ))}
              </div>
            </div>
          )}
        </div>
      )}

      {activeTab === 'overview' && !isSubscription && (
        <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
          <div className="bg-white rounded-lg border border-gray-200 p-6">
            <h3 className="text-sm font-semibold text-gray-500 uppercase mb-4">Resource Information</h3>
            <dl className="space-y-3">
              <DetailRow label="Name" value={resource.resourceName} />
              <DetailRow label="Type" value={resource.resourceType} />
              <DetailRow label="Location" value={resource.location} />
              <DetailRow label="Resource Group" value={resource.resourceGroup} />
              <DetailRow label="Subscription" value={resource.subscriptionId} />
            </dl>
          </div>
          <div className="bg-white rounded-lg border border-gray-200 p-6">
            <h3 className="text-sm font-semibold text-gray-500 uppercase mb-4">SKU & Identity</h3>
            <dl className="space-y-3">
              <DetailRow label="SKU Name" value={resource.skuName || '—'} />
              <DetailRow label="SKU Tier" value={resource.skuTier || '—'} />
              <DetailRow label="SKU Capacity" value={resource.skuCapacity?.toString() || '—'} />
              <DetailRow label="Identity Type" value={resource.identityType || 'None'} />
            </dl>
          </div>
          {resource.tags && Object.keys(resource.tags).length > 0 && (
            <div className="bg-white rounded-lg border border-gray-200 p-6 md:col-span-2">
              <h3 className="text-sm font-semibold text-gray-500 uppercase mb-4">Tags</h3>
              <div className="flex flex-wrap gap-2">
                {Object.entries(resource.tags).map(([key, value]) => (
                  <span key={key} className="inline-flex items-center px-3 py-1 rounded-full text-xs font-medium bg-gray-100 text-gray-800">
                    {key}: {value}
                  </span>
                ))}
              </div>
            </div>
          )}
        </div>
      )}

      {activeTab === 'properties' && properties && (
        <div className="bg-white rounded-lg border border-gray-200 p-6">
          <h3 className="text-sm font-semibold text-gray-500 uppercase mb-4">ARM Properties</h3>
          <pre className="bg-gray-50 p-4 rounded-lg overflow-auto text-xs max-h-[600px] font-mono">
            {JSON.stringify(properties, null, 2)}
          </pre>
        </div>
      )}

      {activeTab === 'networking' && networking && (
        <div className="bg-white rounded-lg border border-gray-200 p-6">
          <h3 className="text-sm font-semibold text-gray-500 uppercase mb-4">Networking Configuration</h3>
          <pre className="bg-gray-50 p-4 rounded-lg overflow-auto text-xs max-h-[600px] font-mono">
            {JSON.stringify(networking, null, 2)}
          </pre>
        </div>
      )}

      {activeTab === 'security' && security && (
        <div className="bg-white rounded-lg border border-gray-200 p-6">
          <h3 className="text-sm font-semibold text-gray-500 uppercase mb-4">Security Configuration</h3>
          <pre className="bg-gray-50 p-4 rounded-lg overflow-auto text-xs max-h-[600px] font-mono">
            {JSON.stringify(security, null, 2)}
          </pre>
        </div>
      )}

      {activeTab === 'changes' && (
        <div className="bg-white rounded-lg border border-gray-200">
          <div className="p-6 border-b border-gray-200">
            <h3 className="text-sm font-semibold text-gray-500 uppercase">Recent Changes</h3>
          </div>
          {changes.length === 0 ? (
            <p className="p-6 text-gray-500 text-center">No changes detected for this resource.</p>
          ) : (
            <div className="divide-y divide-gray-200">
              {changes.map(change => (
                <div key={change.changeId} className="p-4 hover:bg-gray-50">
                  <div className="flex items-center justify-between mb-2">
                    <div className="flex items-center gap-3">
                      <span className={`px-2 py-0.5 rounded text-xs font-medium ${
                        change.changeType === 'Create' ? 'bg-green-100 text-green-800' :
                        change.changeType === 'Delete' ? 'bg-red-100 text-red-800' :
                        'bg-yellow-100 text-yellow-800'
                      }`}>
                        {change.changeType}
                      </span>
                      <span className={`px-2 py-0.5 rounded text-xs ${
                        change.classification === 'security' ? 'bg-red-50 text-red-700' :
                        change.classification === 'cost' ? 'bg-amber-50 text-amber-700' :
                        change.classification === 'governance' ? 'bg-purple-50 text-purple-700' :
                        'bg-gray-50 text-gray-700'
                      }`}>
                        {change.classification}
                      </span>
                    </div>
                    <span className="text-xs text-gray-500">{new Date(change.detectedAt).toLocaleString()}</span>
                  </div>
                  {change.actorName && (
                    <p className="text-sm text-gray-600">By: {change.actorName} {change.clientType && `via ${change.clientType}`}</p>
                  )}
                  {change.changedProperties && change.changedProperties.length > 0 && (
                    <div className="mt-2 space-y-1">
                      {change.changedProperties.slice(0, 5).map((prop, i) => (
                        <div key={i} className="text-xs font-mono bg-gray-50 rounded p-2">
                          <span className="text-gray-500">{prop.property}:</span>{' '}
                          <span className="text-red-600 line-through">{prop.oldValue || '(empty)'}</span>{' → '}
                          <span className="text-green-600">{prop.newValue || '(empty)'}</span>
                        </div>
                      ))}
                    </div>
                  )}
                </div>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

function DetailRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex justify-between">
      <dt className="text-sm text-gray-500">{label}</dt>
      <dd className="text-sm font-medium text-gray-900 text-right break-all max-w-[60%]">{value}</dd>
    </div>
  );
}

export default function ResourceDetailPage() {
  return (
    <Suspense fallback={<div className="flex items-center justify-center h-64"><div className="animate-spin rounded-full h-8 w-8 border-b-2 border-blue-600" /></div>}>
      <ResourceDetailContent />
    </Suspense>
  );
}
