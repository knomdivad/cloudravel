'use client';

import React, { useState } from 'react';
import { useTenantContext } from '../../contexts/TenantContext';
import { useAdvisorRecommendations, usePolicyCompliance } from '../../lib/hooks';
import type { Recommendation } from '../../lib/types';

const ADVISOR_CATEGORIES = ['Cost', 'Security', 'Reliability', 'OperationalExcellence', 'Performance'];

export default function GovernancePage() {
  const { tenantId } = useTenantContext();
  const [activeTab, setActiveTab] = useState<'advisor' | 'policy'>('advisor');
  const [categoryFilter, setCategoryFilter] = useState('');

  const { data: advisorData, isLoading: advLoading } = useAdvisorRecommendations(tenantId, {
    category: categoryFilter || undefined,
    status: 'active',
    limit: 200,
  });
  const { data: policyData, isLoading: polLoading } = usePolicyCompliance(tenantId, {
    state: 'NonCompliant',
    limit: 200,
  });

  const advisorRecs = advisorData?.recommendations ?? [];
  const policyRecs = policyData?.records ?? [];

  const totalSavings = advisorRecs
    .filter((r) => r.category === 'Cost')
    .reduce((sum, r) => sum + (r.estimatedSavings ?? 0), 0);

  if (!tenantId) {
    return <div className="text-center py-20 text-gray-500">Select a tenant to view governance.</div>;
  }

  return (
    <div className="space-y-5">
      <div>
        <h1 className="text-xl font-bold">Governance & Compliance</h1>
        <p className="text-sm text-gray-500 mt-1">Azure Advisor recommendations and Policy compliance</p>
      </div>

      {/* Summary Cards */}
      <div className="grid grid-cols-1 md:grid-cols-4 gap-4">
        <div className="bg-white rounded-xl shadow-sm border border-gray-200 border-l-4 border-l-azure-500 p-4">
          <p className="text-xs text-gray-500 uppercase">Advisor Active</p>
          <p className="text-2xl font-bold mt-1">{advisorRecs.length}</p>
        </div>
        <div className="bg-white rounded-xl shadow-sm border border-gray-200 border-l-4 border-l-green-500 p-4">
          <p className="text-xs text-gray-500 uppercase">Potential Savings</p>
          <p className="text-2xl font-bold mt-1">${totalSavings.toLocaleString()}/mo</p>
        </div>
        <div className="bg-white rounded-xl shadow-sm border border-gray-200 border-l-4 border-l-purple-500 p-4">
          <p className="text-xs text-gray-500 uppercase">Non-Compliant</p>
          <p className="text-2xl font-bold mt-1">{policyRecs.length}</p>
        </div>
        <div className="bg-white rounded-xl shadow-sm border border-gray-200 border-l-4 border-l-amber-500 p-4">
          <p className="text-xs text-gray-500 uppercase">Categories</p>
          <p className="text-2xl font-bold mt-1">{new Set(advisorRecs.map((r) => r.category)).size}</p>
        </div>
      </div>

      {/* Tabs */}
      <div className="flex gap-1 bg-gray-100 rounded-lg p-1 w-fit">
        <button
          onClick={() => setActiveTab('advisor')}
          className={`px-4 py-2 text-sm rounded-md transition-colors ${
            activeTab === 'advisor' ? 'bg-white shadow text-gray-900 font-medium' : 'text-gray-500'
          }`}
        >
          Advisor ({advisorRecs.length})
        </button>
        <button
          onClick={() => setActiveTab('policy')}
          className={`px-4 py-2 text-sm rounded-md transition-colors ${
            activeTab === 'policy' ? 'bg-white shadow text-gray-900 font-medium' : 'text-gray-500'
          }`}
        >
          Policy ({policyRecs.length})
        </button>
      </div>

      {/* Advisor Tab */}
      {activeTab === 'advisor' && (
        <>
          <div className="flex items-center gap-3 flex-wrap">
            {ADVISOR_CATEGORIES.map((cat) => {
              const count = advisorRecs.filter((r) => r.category === cat).length;
              return (
                <button
                  key={cat}
                  onClick={() => setCategoryFilter(categoryFilter === cat ? '' : cat)}
                  className={`text-xs px-3 py-1.5 rounded-full border transition-colors ${
                    categoryFilter === cat
                      ? 'bg-azure-100 border-azure-300 text-azure-700'
                      : 'border-gray-300 text-gray-600 hover:border-gray-400'
                  }`}
                >
                  {cat} ({count})
                </button>
              );
            })}
          </div>

          <div className="bg-white rounded-xl shadow-sm border border-gray-200 overflow-hidden">
            {advLoading ? (
              <div className="p-12 text-center text-gray-400">Loading...</div>
            ) : (
              <div className="overflow-x-auto">
                <table className="w-full text-sm">
                  <thead className="bg-gray-50">
                    <tr className="text-left text-gray-500">
                      <th className="px-4 py-3 font-medium">Recommendation</th>
                      <th className="px-4 py-3 font-medium">Category</th>
                      <th className="px-4 py-3 font-medium">Severity</th>
                      <th className="px-4 py-3 font-medium">Resource</th>
                      <th className="px-4 py-3 font-medium">Savings</th>
                      <th className="px-4 py-3 font-medium">Age</th>
                    </tr>
                  </thead>
                  <tbody>
                    {advisorRecs.map((r) => (
                      <tr key={r.id} className="border-t border-gray-100 hover:bg-gray-50">
                        <td className="px-4 py-3">
                          <p className="font-medium text-gray-900">{r.title}</p>
                          {r.description && (
                            <p className="text-xs text-gray-500 mt-0.5 truncate max-w-md">{r.description}</p>
                          )}
                        </td>
                        <td className="px-4 py-3">
                          <CategoryBadge category={r.category} />
                        </td>
                        <td className="px-4 py-3">
                          <SeverityBadge severity={r.severity} />
                        </td>
                        <td className="px-4 py-3 text-gray-600 text-xs font-mono truncate max-w-[200px]">
                          {r.resourceId?.split('/').pop() ?? '—'}
                        </td>
                        <td className="px-4 py-3 text-green-600 font-medium">
                          {r.estimatedSavings ? `$${r.estimatedSavings.toLocaleString()}` : '—'}
                        </td>
                        <td className="px-4 py-3 text-gray-500 text-xs whitespace-nowrap">
                          {daysSince(r.firstSeenAt)}d
                        </td>
                      </tr>
                    ))}
                    {advisorRecs.length === 0 && (
                      <tr>
                        <td colSpan={6} className="px-4 py-12 text-center text-gray-400">
                          No advisor recommendations found.
                        </td>
                      </tr>
                    )}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        </>
      )}

      {/* Policy Tab */}
      {activeTab === 'policy' && (
        <div className="bg-white rounded-xl shadow-sm border border-gray-200 overflow-hidden">
          {polLoading ? (
            <div className="p-12 text-center text-gray-400">Loading...</div>
          ) : (
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead className="bg-gray-50">
                  <tr className="text-left text-gray-500">
                    <th className="px-4 py-3 font-medium">Policy</th>
                    <th className="px-4 py-3 font-medium">Category</th>
                    <th className="px-4 py-3 font-medium">Severity</th>
                    <th className="px-4 py-3 font-medium">Resource</th>
                    <th className="px-4 py-3 font-medium">Last Evaluated</th>
                  </tr>
                </thead>
                <tbody>
                  {policyRecs.map((r: any, i: number) => (
                    <tr key={i} className="border-t border-gray-100 hover:bg-gray-50">
                      <td className="px-4 py-3 font-medium text-gray-900">{r.title ?? r.policyDefinitionName}</td>
                      <td className="px-4 py-3 text-gray-600">{r.category ?? '—'}</td>
                      <td className="px-4 py-3">
                        <SeverityBadge severity={r.severity ?? 'medium'} />
                      </td>
                      <td className="px-4 py-3 text-gray-600 text-xs font-mono truncate max-w-[200px]">
                        {r.resourceId?.split('/').pop() ?? '—'}
                      </td>
                      <td className="px-4 py-3 text-gray-500 text-xs">
                        {r.lastSeenAt ? new Date(r.lastSeenAt).toLocaleDateString() : '—'}
                      </td>
                    </tr>
                  ))}
                  {policyRecs.length === 0 && (
                    <tr>
                      <td colSpan={5} className="px-4 py-12 text-center text-gray-400">
                        All policies are compliant. Great job!
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

function CategoryBadge({ category }: { category: string }) {
  const colorMap: Record<string, string> = {
    Cost: 'bg-green-100 text-green-700',
    Security: 'bg-red-100 text-red-700',
    Reliability: 'bg-blue-100 text-blue-700',
    OperationalExcellence: 'bg-purple-100 text-purple-700',
    Performance: 'bg-amber-100 text-amber-700',
  };
  return (
    <span className={`text-xs font-medium px-2 py-0.5 rounded-full ${colorMap[category] ?? 'bg-gray-100 text-gray-600'}`}>
      {category}
    </span>
  );
}

function SeverityBadge({ severity }: { severity: string }) {
  const colorMap: Record<string, string> = {
    critical: 'bg-red-100 text-red-700',
    high: 'bg-orange-100 text-orange-700',
    medium: 'bg-yellow-100 text-yellow-700',
    low: 'bg-blue-100 text-blue-700',
    info: 'bg-gray-100 text-gray-600',
  };
  return (
    <span className={`text-xs font-medium px-2 py-0.5 rounded-full ${colorMap[severity?.toLowerCase()] ?? 'bg-gray-100 text-gray-600'}`}>
      {severity}
    </span>
  );
}

function daysSince(dateStr: string): number {
  return Math.floor((Date.now() - new Date(dateStr).getTime()) / (1000 * 60 * 60 * 24));
}
