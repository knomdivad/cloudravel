'use client';

import React, { useMemo } from 'react';
import {
  useInventorySummary,
  useChanges,
  useAdvisorRecommendations,
  useDefenderFindings,
  usePolicyCompliance,
  useChangeTimeline,
} from '../lib/hooks';
import { useTenantContext } from '../contexts/TenantContext';
import {
  BarChart,
  Bar,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  ResponsiveContainer,
  PieChart,
  Pie,
  Cell,
  Legend,
} from 'recharts';

const SEVERITY_COLORS: Record<string, string> = {
  critical: '#dc2626',
  high: '#ea580c',
  medium: '#ca8a04',
  low: '#2563eb',
  info: '#6b7280',
};

const CLASSIFICATION_COLORS: Record<string, string> = {
  security: '#dc2626',
  governance: '#7c3aed',
  cost: '#059669',
  operational: '#2563eb',
};

export default function DashboardPage() {
  const { tenantId, currentTenant } = useTenantContext();

  // Memoize date boundaries so SWR cache keys remain stable across re-renders
  const { dayAgoIso, weekAgoIso, nowIso } = useMemo(() => {
    const n = new Date();
    return {
      dayAgoIso: new Date(n.getTime() - 24 * 60 * 60 * 1000).toISOString(),
      weekAgoIso: new Date(n.getTime() - 7 * 24 * 60 * 60 * 1000).toISOString(),
      nowIso: n.toISOString(),
    };
  }, []);

  const { data: inventorySummary, isLoading: invLoading } = useInventorySummary(tenantId);
  const { data: recentChanges, isLoading: chgLoading } = useChanges(tenantId, {
    from: dayAgoIso,
    limit: 10,
  });
  const { data: timeline, isLoading: tlLoading } = useChangeTimeline(
    tenantId,
    weekAgoIso,
    nowIso
  );
  const { data: advisorData } = useAdvisorRecommendations(tenantId, { status: 'active', limit: 100 });
  const { data: defenderData } = useDefenderFindings(tenantId, { status: 'Unhealthy', limit: 100 });
  const { data: policyData } = usePolicyCompliance(tenantId, { state: 'NonCompliant', limit: 100 });

  // Pie chart: by cloud when 2+ clouds have inventory; otherwise by service family.
  // Must be before any early returns to satisfy React's rules of hooks.
  const pieChart = useMemo(() => {
    const byProvider = (inventorySummary?.byProvider ?? []).filter((p) => p.count > 0);
    // API may return camelCase or PascalCase
    const providers = byProvider.length
      ? byProvider
      : normalizeByProvider(inventorySummary);

    if (providers.length > 1) {
      const data = providers
        .map((p) => ({
          name: cloudDisplayName(p.provider),
          count: p.count,
          fill: CLOUD_COLORS[p.provider.toLowerCase()] ?? '#6b7280',
        }))
        .sort((a, b) => b.count - a.count);
      return { mode: 'cloud' as const, title: 'Resources by cloud', data };
    }

    const types = inventorySummary?.resourceTypes ?? [];
    const familyMap = new Map<string, number>();
    for (const rt of types) {
      const family = serviceFamilyName(rt.resourceType);
      familyMap.set(family, (familyMap.get(family) ?? 0) + rt.count);
    }
    let sorted = Array.from(familyMap.entries())
      .map(([name, count]) => ({ name, count, fill: '' }))
      .sort((a, b) => b.count - a.count);
    if (sorted.length > 8) {
      const top = sorted.slice(0, 7);
      const otherCount = sorted.slice(7).reduce((sum, s) => sum + s.count, 0);
      sorted = [...top, { name: 'Other', count: otherCount, fill: '' }];
    }
    const data = sorted.map((s, i) => ({
      ...s,
      fill: FAMILY_PIE_COLORS[i % FAMILY_PIE_COLORS.length],
    }));
    const cloudHint =
      providers.length === 1 ? cloudDisplayName(providers[0].provider) : null;
    return {
      mode: 'family' as const,
      title: cloudHint ? `Service families (${cloudHint})` : 'Service families',
      data,
    };
  }, [inventorySummary]);

  if (!tenantId) {
    return (
      <div className="text-center py-20 text-gray-500">
        <p className="text-lg">No tenant selected</p>
        <p className="text-sm mt-2">Select a tenant from the sidebar to view the dashboard.</p>
      </div>
    );
  }

  const totalResources = inventorySummary?.totalResources ?? 0;
  const totalChanges = recentChanges?.changes?.length ?? 0;
  const advisorCount = advisorData?.recommendations?.length ?? 0;
  const defenderCount = defenderData?.findings?.length ?? 0;
  const policyCount = policyData?.records?.length ?? 0;

  // Compute savings from advisor data
  const estimatedSavings =
    advisorData?.recommendations
      ?.filter((r) => r.category === 'Cost')
      .reduce((sum, r) => sum + (r.estimatedSavings ?? 0), 0) ?? 0;

  return (
    <div className="space-y-6">
      {/* Summary Cards */}
      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-5 gap-4">
        <SummaryCard
          title="Total Resources"
          value={totalResources.toLocaleString()}
          loading={invLoading}
          color="azure"
        />
        <SummaryCard
          title="Changes (24h)"
          value={totalChanges.toString()}
          loading={chgLoading}
          color="amber"
        />
        <SummaryCard
          title="Advisor Recommendations"
          value={advisorCount.toString()}
          subtitle={estimatedSavings > 0 ? `~$${estimatedSavings.toLocaleString()}/mo savings` : undefined}
          color="green"
        />
        <SummaryCard
          title="Defender Findings"
          value={defenderCount.toString()}
          color="red"
        />
        <SummaryCard
          title="Policy Non-Compliance"
          value={policyCount.toString()}
          color="purple"
        />
      </div>

      {/* Charts Row */}
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
        {/* Change Timeline */}
        <div className="bg-white rounded-xl shadow-sm border border-gray-200 p-5">
          <h3 className="text-sm font-semibold text-gray-700 mb-4">Change Activity (7 days)</h3>
          {tlLoading ? (
            <div className="h-64 flex items-center justify-center text-gray-400">Loading...</div>
          ) : (
            <ResponsiveContainer width="100%" height={260}>
              <BarChart data={timeline?.buckets ?? []} margin={{ top: 5, right: 10, left: 0, bottom: 5 }}>
                <CartesianGrid strokeDasharray="3 3" stroke="#f0f0f0" />
                <XAxis
                  dataKey="bucketStart"
                  tickFormatter={(v) => new Date(v).toLocaleDateString(undefined, { weekday: 'short' })}
                  fontSize={12}
                />
                <YAxis fontSize={12} />
                <Tooltip
                  labelFormatter={(v) => new Date(v).toLocaleDateString()}
                  contentStyle={{ fontSize: 12 }}
                />
                <Bar dataKey="security" stackId="a" fill={CLASSIFICATION_COLORS.security} name="Security" />
                <Bar dataKey="governance" stackId="a" fill={CLASSIFICATION_COLORS.governance} name="Governance" />
                <Bar dataKey="cost" stackId="a" fill={CLASSIFICATION_COLORS.cost} name="Cost" />
                <Bar dataKey="operational" stackId="a" fill={CLASSIFICATION_COLORS.operational} name="Operational" />
              </BarChart>
            </ResponsiveContainer>
          )}
        </div>

        {/* Resource Breakdown: by cloud (multi-cloud) or service family (single cloud) */}
        <div className="bg-white rounded-xl shadow-sm border border-gray-200 p-5">
          <h3 className="text-sm font-semibold text-gray-700 mb-4">{pieChart.title}</h3>
          {invLoading ? (
            <div className="h-64 flex items-center justify-center text-gray-400">Loading...</div>
          ) : pieChart.data.length === 0 ? (
            <div className="h-64 flex items-center justify-center text-gray-400 text-sm">
              No inventory yet. Collect clouds to see the breakdown.
            </div>
          ) : (
            <ResponsiveContainer width="100%" height={260}>
              <PieChart>
                <Pie
                  data={pieChart.data}
                  dataKey="count"
                  nameKey="name"
                  cx="50%"
                  cy="50%"
                  outerRadius={90}
                  label={({ name, percent }) =>
                    `${name} (${((percent ?? 0) * 100).toFixed(0)}%)`
                  }
                  labelLine={false}
                  fontSize={10}
                >
                  {pieChart.data.map((entry, i) => (
                    <Cell key={i} fill={entry.fill || FAMILY_PIE_COLORS[i % FAMILY_PIE_COLORS.length]} />
                  ))}
                </Pie>
                <Tooltip
                  formatter={(value: number, _name, props) => {
                    const total = pieChart.data.reduce((s, d) => s + d.count, 0);
                    const pct = total > 0 ? ((Number(value) / total) * 100).toFixed(1) : '0';
                    return [`${Number(value).toLocaleString()} (${pct}%)`, props?.payload?.name ?? ''];
                  }}
                />
                <Legend />
              </PieChart>
            </ResponsiveContainer>
          )}
        </div>
      </div>

      {/* Recent Changes */}
      <div className="bg-white rounded-xl shadow-sm border border-gray-200 p-5">
        <div className="flex items-center justify-between mb-4">
          <h3 className="text-sm font-semibold text-gray-700">Recent Changes (24h)</h3>
          <a href="/changes" className="text-xs text-azure-600 hover:underline">
            View all →
          </a>
        </div>
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-gray-100 text-left text-gray-500">
                <th className="pb-2 font-medium">Resource</th>
                <th className="pb-2 font-medium">Type</th>
                <th className="pb-2 font-medium">Change</th>
                <th className="pb-2 font-medium">Classification</th>
                <th className="pb-2 font-medium">Actor</th>
                <th className="pb-2 font-medium">Time</th>
              </tr>
            </thead>
            <tbody>
              {(recentChanges?.changes ?? []).map((c) => (
                <tr key={c.changeId} className="border-b border-gray-50 hover:bg-gray-50">
                  <td className="py-2 font-mono text-xs truncate max-w-xs" title={c.resourceId}>
                    {c.resourceId.split('/').pop()}
                  </td>
                  <td className="py-2 text-gray-600">{c.resourceType.split('/').pop()}</td>
                  <td className="py-2">
                    <span className={`classification-${c.changeType.toLowerCase()}`}>
                      {c.changeType}
                    </span>
                  </td>
                  <td className="py-2">
                    <span className={`classification-${c.classification}`}>
                      {c.classification}
                    </span>
                  </td>
                  <td className="py-2 text-gray-600">{c.actorName ?? 'System'}</td>
                  <td className="py-2 text-gray-500 text-xs">
                    {new Date(c.detectedAt).toLocaleTimeString()}
                  </td>
                </tr>
              ))}
              {totalChanges === 0 && (
                <tr>
                  <td colSpan={6} className="py-8 text-center text-gray-400">
                    No changes detected in the last 24 hours.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}

// ============================================================================
// Pie chart helpers
// ============================================================================

const CLOUD_COLORS: Record<string, string> = {
  azure: '#0078d4',
  aws: '#ff9900',
  gcp: '#34a853',
};

const FAMILY_PIE_COLORS = [
  '#0078d4', '#005a9e', '#004578', '#106ebe', '#2b88d8', '#71afe5', '#c7e0f4', '#deecf9',
  '#ff9900', '#34a853', '#7c3aed',
];

function cloudDisplayName(provider: string): string {
  const p = provider.toLowerCase();
  if (p === 'aws') return 'AWS';
  if (p === 'gcp') return 'GCP';
  if (p === 'azure') return 'Azure';
  return provider;
}

/** Azure Microsoft.X / AWS service / GCP *.googleapis.com service → family label. */
function serviceFamilyName(resourceType: string): string {
  const az = resourceType.match(/^Microsoft\.(\w+)/i);
  if (az) return az[1];

  if (resourceType.includes('.googleapis.com/')) {
    const svc = resourceType.split('.googleapis.com/')[0] || resourceType;
    return svc.charAt(0).toUpperCase() + svc.slice(1);
  }

  // AWS: "ec2/instance" or "s3"
  const head = resourceType.split('/')[0] || resourceType;
  if (!head) return 'Other';
  return head.length <= 4 ? head.toUpperCase() : head.charAt(0).toUpperCase() + head.slice(1);
}

function normalizeByProvider(
  summary: { byProvider?: { provider: string; count: number }[]; ByProvider?: { provider?: string; Provider?: string; count?: number; Count?: number }[] } | undefined
): { provider: string; count: number }[] {
  const raw = summary?.byProvider ?? summary?.ByProvider ?? [];
  return raw
    .map((r) => ({
      provider: String((r as { provider?: string; Provider?: string }).provider
        ?? (r as { Provider?: string }).Provider
        ?? ''),
      count: Number((r as { count?: number; Count?: number }).count
        ?? (r as { Count?: number }).Count
        ?? 0),
    }))
    .filter((r) => r.provider && r.count > 0);
}

// ============================================================================
// Summary Card Component
// ============================================================================

function SummaryCard({
  title,
  value,
  subtitle,
  loading,
  color = 'azure',
}: {
  title: string;
  value: string;
  subtitle?: string;
  loading?: boolean;
  color?: 'azure' | 'green' | 'red' | 'amber' | 'purple';
}) {
  const colorMap = {
    azure: 'border-l-azure-500',
    green: 'border-l-green-500',
    red: 'border-l-red-500',
    amber: 'border-l-amber-500',
    purple: 'border-l-purple-500',
  };

  return (
    <div className={`bg-white rounded-xl shadow-sm border border-gray-200 border-l-4 ${colorMap[color]} p-4`}>
      <p className="text-xs text-gray-500 uppercase tracking-wide">{title}</p>
      {loading ? (
        <div className="h-8 w-16 bg-gray-100 animate-pulse rounded mt-1" />
      ) : (
        <>
          <p className="text-2xl font-bold mt-1">{value}</p>
          {subtitle && <p className="text-xs text-gray-500 mt-1">{subtitle}</p>}
        </>
      )}
    </div>
  );
}
