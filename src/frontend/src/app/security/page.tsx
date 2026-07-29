'use client';

import React, { useState } from 'react';
import { useTenantContext } from '../../contexts/TenantContext';
import { useDefenderFindings } from '../../lib/hooks';
import { ProviderBadge, resolveCloudProvider, changeResourceLeafName } from '../../lib/cloud-scope';
import type { Recommendation } from '../../lib/types';

const SEVERITY_ORDER = ['critical', 'high', 'medium', 'low', 'info'];

const SEVERITY_COLORS: Record<string, { bg: string; text: string; dot: string }> = {
  critical: { bg: 'bg-red-50', text: 'text-red-700', dot: 'bg-red-500' },
  high: { bg: 'bg-orange-50', text: 'text-orange-700', dot: 'bg-orange-500' },
  medium: { bg: 'bg-yellow-50', text: 'text-yellow-700', dot: 'bg-yellow-500' },
  low: { bg: 'bg-blue-50', text: 'text-blue-700', dot: 'bg-blue-500' },
  info: { bg: 'bg-gray-50', text: 'text-gray-600', dot: 'bg-gray-400' },
};

export default function SecurityPage() {
  const { tenantId } = useTenantContext();
  const [severityFilter, setSeverityFilter] = useState('');
  const [statusFilter, setStatusFilter] = useState('');
  const [expandedId, setExpandedId] = useState<string | null>(null);

  const { data, isLoading, error } = useDefenderFindings(tenantId, {
    severity: severityFilter || undefined,
    status: statusFilter || undefined,
    limit: 200,
  });

  const findings = data?.findings ?? [];

  // Compute severity counts for quick stats
  const severityCounts = SEVERITY_ORDER.reduce<Record<string, number>>((acc, sev) => {
    acc[sev] = findings.filter((f) => f.severity?.toLowerCase() === sev).length;
    return acc;
  }, {});

  if (!tenantId) {
    return <div className="text-center py-20 text-gray-500">Select a tenant to view security posture.</div>;
  }

  return (
    <div className="space-y-5">
      <div>
        <h1 className="text-xl font-bold">Security Posture</h1>
        <p className="text-sm text-gray-500 mt-1">
          Azure Defender for Cloud, AWS Security Hub + open security groups / S3 public access, and
          Google Security Command Center — plus inventory posture checks after multi-cloud collect.
        </p>
      </div>

      {/* Severity Summary Cards */}
      <div className="grid grid-cols-2 md:grid-cols-5 gap-3">
        {SEVERITY_ORDER.map((sev) => {
          const colors = SEVERITY_COLORS[sev];
          return (
            <button
              key={sev}
              onClick={() => setSeverityFilter(severityFilter === sev ? '' : sev)}
              className={`rounded-xl border p-4 text-left transition-all ${
                severityFilter === sev
                  ? `${colors.bg} border-current ring-2 ring-offset-1`
                  : 'bg-white border-gray-200 hover:border-gray-300'
              }`}
            >
              <div className="flex items-center gap-2 mb-1">
                <div className={`w-2.5 h-2.5 rounded-full ${colors.dot}`} />
                <span className={`text-xs font-medium uppercase ${colors.text}`}>{sev}</span>
              </div>
              <p className="text-2xl font-bold">{severityCounts[sev]}</p>
            </button>
          );
        })}
      </div>

      {/* Status Filter */}
      <div className="flex items-center gap-3">
        <select
          value={statusFilter}
          onChange={(e) => setStatusFilter(e.target.value)}
          className="border border-gray-300 rounded-lg px-3 py-1.5 text-sm focus:border-azure-500 focus:outline-none"
        >
          <option value="">All Statuses</option>
          <option value="Unhealthy">Unhealthy</option>
          <option value="Healthy">Healthy</option>
          <option value="NotApplicable">Not Applicable</option>
        </select>
        <span className="text-sm text-gray-500">{findings.length} findings</span>
      </div>

      {/* Findings List */}
      <div className="space-y-3">
        {isLoading ? (
          <div className="bg-white rounded-xl border border-gray-200 p-12 text-center text-gray-400">
            Loading findings...
          </div>
        ) : error ? (
          <div className="bg-white rounded-xl border border-gray-200 p-12 text-center text-red-500">
            Error: {error.message}
          </div>
        ) : findings.length === 0 ? (
          <div className="bg-white rounded-xl border border-gray-200 p-12 text-center text-gray-400">
            No findings match the current filters.
          </div>
        ) : (
          findings.map((f) => (
            <FindingCard
              key={f.id}
              finding={f}
              expanded={expandedId === f.id}
              onToggle={() => setExpandedId(expandedId === f.id ? null : f.id)}
            />
          ))
        )}
      </div>
    </div>
  );
}

function FindingCard({
  finding,
  expanded,
  onToggle,
}: {
  finding: Recommendation;
  expanded: boolean;
  onToggle: () => void;
}) {
  const sev = finding.severity?.toLowerCase() ?? 'info';
  const colors = SEVERITY_COLORS[sev] ?? SEVERITY_COLORS.info;

  return (
    <div
      className={`bg-white rounded-xl border border-gray-200 overflow-hidden transition-shadow ${
        expanded ? 'shadow-md' : 'hover:shadow-sm'
      }`}
    >
      <button
        onClick={onToggle}
        className="w-full text-left px-5 py-4 flex items-start gap-4"
      >
        <div className={`w-2.5 h-2.5 rounded-full mt-1.5 flex-shrink-0 ${colors.dot}`} />
        <div className="flex-1 min-w-0">
          <div className="flex flex-wrap items-center gap-2">
            <p className="font-medium text-gray-900">{finding.title}</p>
            <ProviderBadge
              provider={resolveCloudProvider({
                provider: finding.provider,
                resourceId: finding.resourceId,
                id: finding.id,
                text: [finding.title, finding.description, finding.category].filter(Boolean).join('\n'),
              })}
            />
          </div>
          <div className="flex items-center gap-3 mt-1">
            <span className={`text-xs font-medium ${colors.text} uppercase`}>{sev}</span>
            <span className="text-xs text-gray-400">|</span>
            <span className="text-xs text-gray-500">{finding.category}</span>
            {finding.resourceId && (
              <>
                <span className="text-xs text-gray-400">|</span>
                <span className="text-xs text-gray-500 truncate" title={finding.resourceId}>
                  {changeResourceLeafName(finding.resourceId)}
                </span>
              </>
            )}
          </div>
        </div>
        <div className="text-xs text-gray-400 whitespace-nowrap mt-1">
          {new Date(finding.lastSeenAt).toLocaleDateString()}
        </div>
        <svg
          className={`w-4 h-4 text-gray-400 mt-1 transition-transform ${expanded ? 'rotate-180' : ''}`}
          fill="none" viewBox="0 0 24 24" strokeWidth="2" stroke="currentColor"
        >
          <path strokeLinecap="round" strokeLinejoin="round" d="m19.5 8.25-7.5 7.5-7.5-7.5" />
        </svg>
      </button>

      {expanded && (
        <div className="px-5 pb-5 border-t border-gray-100 pt-4 space-y-3">
          {finding.description && (
            <div>
              <p className="text-xs font-medium text-gray-500 mb-1">Description</p>
              <p className="text-sm text-gray-700">{finding.description}</p>
            </div>
          )}
          {finding.remediationAction && (
            <div>
              <p className="text-xs font-medium text-gray-500 mb-1">Remediation</p>
              <p className="text-sm text-gray-700">{finding.remediationAction}</p>
            </div>
          )}
          {finding.resourceId && (
            <div>
              <p className="text-xs font-medium text-gray-500 mb-1">Resource</p>
              <p className="text-sm text-gray-700 font-mono break-all">{finding.resourceId}</p>
            </div>
          )}
          <div className="flex items-center gap-4 text-xs text-gray-500">
            <span>First seen: {new Date(finding.firstSeenAt).toLocaleDateString()}</span>
            <span>Last seen: {new Date(finding.lastSeenAt).toLocaleDateString()}</span>
            <span>Status: {finding.status}</span>
          </div>
        </div>
      )}
    </div>
  );
}
