'use client';

import React, { useState, useMemo } from 'react';
import { useTenantContext } from '../../contexts/TenantContext';
import { useChanges, useChangeTimeline } from '../../lib/hooks';
import {
  BarChart,
  Bar,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  ResponsiveContainer,
} from 'recharts';

const CLASSIFICATION_COLORS: Record<string, string> = {
  security: '#dc2626',
  governance: '#7c3aed',
  cost: '#059669',
  operational: '#2563eb',
};

const PAGE_SIZE = 30;

export default function ChangesPage() {
  const { tenantId } = useTenantContext();

  const [timeRange, setTimeRange] = useState<'24h' | '7d' | '30d'>('7d');
  const [classification, setClassification] = useState('');
  const [page, setPage] = useState(0);

  // Memoize date boundaries so SWR cache keys remain stable across re-renders
  const fromDate = useMemo(() => {
    const d = new Date();
    if (timeRange === '24h') d.setHours(d.getHours() - 24);
    else if (timeRange === '7d') d.setDate(d.getDate() - 7);
    else d.setDate(d.getDate() - 30);
    return d.toISOString();
  }, [timeRange]);

  const { weekAgoIso, nowIso } = useMemo(() => {
    const n = new Date();
    return {
      weekAgoIso: new Date(n.getTime() - 7 * 24 * 60 * 60 * 1000).toISOString(),
      nowIso: n.toISOString(),
    };
  }, []);

  const { data: changes, isLoading } = useChanges(tenantId, {
    from: fromDate,
    classification: classification || undefined,
    offset: page * PAGE_SIZE,
    limit: PAGE_SIZE,
  });

  const { data: timeline } = useChangeTimeline(
    tenantId,
    weekAgoIso,
    nowIso
  );

  if (!tenantId) {
    return <div className="text-center py-20 text-gray-500">Select a tenant to view changes.</div>;
  }

  return (
    <div className="space-y-5">
      <h1 className="text-xl font-bold">Change Intelligence</h1>

      {/* Timeline Chart */}
      <div className="bg-white rounded-xl shadow-sm border border-gray-200 p-5">
        <h3 className="text-sm font-semibold text-gray-700 mb-4">Change Volume</h3>
        <ResponsiveContainer width="100%" height={200}>
          <BarChart data={timeline?.buckets ?? []}>
            <CartesianGrid strokeDasharray="3 3" stroke="#f0f0f0" />
            <XAxis
              dataKey="bucketStart"
              tickFormatter={(v) => new Date(v).toLocaleDateString(undefined, { month: 'short', day: 'numeric' })}
              fontSize={12}
            />
            <YAxis fontSize={12} />
            <Tooltip labelFormatter={(v) => new Date(v).toLocaleDateString()} />
            <Bar dataKey="security" stackId="a" fill={CLASSIFICATION_COLORS.security} name="Security" />
            <Bar dataKey="governance" stackId="a" fill={CLASSIFICATION_COLORS.governance} name="Governance" />
            <Bar dataKey="cost" stackId="a" fill={CLASSIFICATION_COLORS.cost} name="Cost" />
            <Bar dataKey="operational" stackId="a" fill={CLASSIFICATION_COLORS.operational} name="Operational" />
          </BarChart>
        </ResponsiveContainer>
      </div>

      {/* Filters */}
      <div className="flex items-center gap-3 flex-wrap">
        <div className="flex gap-1 bg-gray-100 rounded-lg p-1">
          {(['24h', '7d', '30d'] as const).map((r) => (
            <button
              key={r}
              onClick={() => { setTimeRange(r); setPage(0); }}
              className={`px-3 py-1.5 text-sm rounded-md transition-colors ${
                timeRange === r ? 'bg-white shadow text-gray-900 font-medium' : 'text-gray-500 hover:text-gray-700'
              }`}
            >
              {r}
            </button>
          ))}
        </div>

        <select
          value={classification}
          onChange={(e) => { setClassification(e.target.value); setPage(0); }}
          className="border border-gray-300 rounded-lg px-3 py-1.5 text-sm focus:border-azure-500 focus:outline-none"
        >
          <option value="">All Classifications</option>
          <option value="security">Security</option>
          <option value="governance">Governance</option>
          <option value="cost">Cost</option>
          <option value="operational">Operational</option>
        </select>
      </div>

      {/* Changes Table */}
      <div className="bg-white rounded-xl shadow-sm border border-gray-200 overflow-hidden">
        {isLoading ? (
          <div className="p-12 text-center text-gray-400">Loading changes...</div>
        ) : (
          <>
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead className="bg-gray-50">
                  <tr className="text-left text-gray-500">
                    <th className="px-4 py-3 font-medium">Resource</th>
                    <th className="px-4 py-3 font-medium">Change</th>
                    <th className="px-4 py-3 font-medium">Classification</th>
                    <th className="px-4 py-3 font-medium">Changed Properties</th>
                    <th className="px-4 py-3 font-medium">Actor</th>
                    <th className="px-4 py-3 font-medium">Time</th>
                  </tr>
                </thead>
                <tbody>
                  {(changes?.changes ?? []).map((c) => (
                    <tr key={c.changeId} className="border-t border-gray-100 hover:bg-gray-50">
                      <td className="px-4 py-3">
                        <div className="font-medium text-gray-900 truncate max-w-xs" title={c.resourceId}>
                          {c.resourceId.split('/').pop()}
                        </div>
                        <div className="text-xs text-gray-400">{c.resourceType.split('/').pop()}</div>
                      </td>
                      <td className="px-4 py-3">
                        <ChangeTypeBadge type={c.changeType} />
                      </td>
                      <td className="px-4 py-3">
                        <span
                          className="text-xs font-medium px-2 py-0.5 rounded-full"
                          style={{
                            backgroundColor: `${CLASSIFICATION_COLORS[c.classification]}15`,
                            color: CLASSIFICATION_COLORS[c.classification],
                          }}
                        >
                          {c.classification}
                        </span>
                      </td>
                      <td className="px-4 py-3 text-xs">
                        {c.changedProperties && c.changedProperties.length > 0 ? (
                          <div className="space-y-1">
                            {c.changedProperties.slice(0, 3).map((p, i) => (
                              <div key={i} className="font-mono">
                                <span className="text-gray-500">{p.property}:</span>{' '}
                                <span className="text-red-500 line-through">{truncate(p.oldValue, 20)}</span>{' '}
                                → <span className="text-green-600">{truncate(p.newValue, 20)}</span>
                              </div>
                            ))}
                            {c.changedProperties.length > 3 && (
                              <span className="text-gray-400">+{c.changedProperties.length - 3} more</span>
                            )}
                          </div>
                        ) : (
                          <span className="text-gray-300">—</span>
                        )}
                      </td>
                      <td className="px-4 py-3 text-gray-600">{c.actorName ?? 'System'}</td>
                      <td className="px-4 py-3 text-gray-500 text-xs whitespace-nowrap">
                        {new Date(c.detectedAt).toLocaleString()}
                      </td>
                    </tr>
                  ))}
                  {(changes?.changes ?? []).length === 0 && (
                    <tr>
                      <td colSpan={6} className="px-4 py-12 text-center text-gray-400">
                        No changes found for the selected period.
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>

            {/* Pagination */}
            {changes && (changes.pagination?.total ?? 0) > PAGE_SIZE && (
              <div className="flex items-center justify-between px-4 py-3 border-t border-gray-100">
                <p className="text-sm text-gray-500">
                  Page {page + 1} of {Math.ceil(changes.pagination.total / PAGE_SIZE)}
                </p>
                <div className="flex gap-2">
                  <button
                    disabled={page === 0}
                    onClick={() => setPage(page - 1)}
                    className="px-3 py-1.5 text-sm border border-gray-300 rounded-lg disabled:opacity-40 hover:bg-gray-50"
                  >
                    Previous
                  </button>
                  <button
                    disabled={!changes.pagination.hasMore}
                    onClick={() => setPage(page + 1)}
                    className="px-3 py-1.5 text-sm border border-gray-300 rounded-lg disabled:opacity-40 hover:bg-gray-50"
                  >
                    Next
                  </button>
                </div>
              </div>
            )}
          </>
        )}
      </div>
    </div>
  );
}

function ChangeTypeBadge({ type }: { type: string }) {
  const styles: Record<string, string> = {
    Create: 'bg-green-100 text-green-700',
    Update: 'bg-blue-100 text-blue-700',
    Delete: 'bg-red-100 text-red-700',
  };
  return (
    <span className={`text-xs font-medium px-2 py-0.5 rounded-full ${styles[type] ?? 'bg-gray-100 text-gray-600'}`}>
      {type}
    </span>
  );
}

function truncate(value: string | undefined, max: number): string {
  if (!value) return '(null)';
  return value.length > max ? value.slice(0, max) + '…' : value;
}
