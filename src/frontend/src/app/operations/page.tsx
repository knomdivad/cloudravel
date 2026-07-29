'use client';

import React, { useState } from 'react';
import { useTenantContext } from '../../contexts/TenantContext';
import { useOpsSummary, useAnomalies } from '../../lib/hooks';
import { updateAnomalyStatus } from '../../lib/api';
import { ProviderBadge, resolveCloudProvider, changeResourceLeafName } from '../../lib/cloud-scope';
import type { Anomaly, Incident, RemediationAction, CloudAccount } from '../../lib/types';

const SEVERITY_COLORS: Record<string, { bg: string; text: string; dot: string }> = {
  critical: { bg: 'bg-red-50', text: 'text-red-700', dot: 'bg-red-500' },
  high: { bg: 'bg-orange-50', text: 'text-orange-700', dot: 'bg-orange-500' },
  medium: { bg: 'bg-yellow-50', text: 'text-yellow-700', dot: 'bg-yellow-500' },
  low: { bg: 'bg-blue-50', text: 'text-blue-700', dot: 'bg-blue-500' },
  info: { bg: 'bg-gray-50', text: 'text-gray-600', dot: 'bg-gray-400' },
};

const KIND_LABELS: Record<string, string> = {
  ChangeVelocitySpike: 'Change Velocity',
  SecurityPostureRegression: 'Security Regression',
  CostAnomaly: 'Cost Anomaly',
  ConfigurationDrift: 'Config Drift',
  UnusualActorActivity: 'Unusual Actor',
  StaleTelemetry: 'Stale Telemetry',
  ResourceSprawl: 'Resource Sprawl',
};

function anomalyProvider(anomaly: Anomaly): string {
  return resolveCloudProvider({
    provider: anomaly.provider,
    resourceId: anomaly.resourceId,
    text: [anomaly.title, anomaly.description, anomaly.metricName, anomaly.kind].filter(Boolean).join('\n'),
  });
}

function remediationProvider(action: RemediationAction): string {
  return resolveCloudProvider({
    provider: action.provider,
    resourceId: action.resourceId,
    id: action.playbookKey,
    text: [action.title, action.reason, action.playbookKey].filter(Boolean).join('\n'),
  });
}

export default function OperationsPage() {
  const { tenantId } = useTenantContext();
  const { data: summary, isLoading, error, mutate } = useOpsSummary(tenantId);
  const { data: anomalyData, mutate: mutateAnomalies } = useAnomalies(tenantId, {
    status: 'Open',
    limit: 100,
  });
  if (!tenantId) {
    return <div className="text-center py-20 text-gray-500">Select a tenant to view operations.</div>;
  }

  if (isLoading) {
    return (
      <div className="bg-white rounded-xl border border-gray-200 p-12 text-center text-gray-400">
        Loading operational picture...
      </div>
    );
  }

  if (error || !summary) {
    return (
      <div className="bg-white rounded-xl border border-gray-200 p-12 text-center text-red-500">
        Error loading operations summary{error ? `: ${error.message}` : ''}
      </div>
    );
  }

  const anomalies = anomalyData?.anomalies ?? summary.recentAnomalies;

  const handleAnomalyAction = async (anomaly: Anomaly, status: string) => {
    await updateAnomalyStatus(tenantId, anomaly.id, status);
    await Promise.all([mutate(), mutateAnomalies()]);
  };

  return (
    <div className="space-y-6">
      <div className="flex items-start justify-between">
        <div>
          <h1 className="text-xl font-bold">Operations Center</h1>
          <p className="text-sm text-gray-500 mt-1">
            Proactive multi-cloud monitoring, incidents, and remediation
          </p>
        </div>
        <div className="flex items-center gap-2 text-xs">
          <span
            className={`px-2 py-1 rounded-full font-medium ${
              summary.monitoringEnabled ? 'bg-green-100 text-green-700' : 'bg-gray-100 text-gray-500'
            }`}
          >
            {summary.monitoringEnabled ? '● Monitoring active' : '○ Monitoring paused'}
          </span>
          <span className="px-2 py-1 rounded-full font-medium bg-gray-100 text-gray-600">
            Remediation: {summary.autoRemediationMode}
          </span>
        </div>
      </div>

      {/* KPI tiles */}
      <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-6 gap-3">
        <KpiTile label="Open Anomalies" value={summary.openAnomalies} accent={summary.openAnomalies > 0 ? 'text-orange-600' : ''} />
        <KpiTile label="Critical" value={summary.criticalAnomalies} accent={summary.criticalAnomalies > 0 ? 'text-red-600' : ''} />
        <KpiTile label="Open Incidents" value={summary.openIncidents} accent={summary.openIncidents > 0 ? 'text-orange-600' : ''} />
        <KpiTile label="SLA Breached" value={summary.slaBreachedIncidents} accent={summary.slaBreachedIncidents > 0 ? 'text-red-600' : ''} />
        <KpiTile label="Pending Approvals" value={summary.pendingApprovals} accent={summary.pendingApprovals > 0 ? 'text-azure-600' : ''} href="/approvals" />
        <KpiTile
          label="MTTR"
          value={summary.meanTimeToResolveHours != null ? `${summary.meanTimeToResolveHours}h` : '—'}
        />
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
        {/* Anomaly queue */}
        <div className="lg:col-span-2 space-y-3">
          <h2 className="text-sm font-semibold text-gray-700 uppercase tracking-wide">
            Open Anomalies ({anomalies.length})
          </h2>
          {anomalies.length === 0 ? (
            <div className="bg-white rounded-xl border border-gray-200 p-10 text-center text-gray-400">
              No open anomalies — the environment is tracking its baselines. ✓
            </div>
          ) : (
            anomalies.map((a) => (
              <AnomalyCard key={a.id} anomaly={a} onAction={handleAnomalyAction} />
            ))
          )}
        </div>

        {/* Right rail: incidents, remediations, cloud accounts */}
        <div className="space-y-6">
          <section>
            <h2 className="text-sm font-semibold text-gray-700 uppercase tracking-wide mb-3">
              Active Incidents
            </h2>
            <div className="space-y-2">
              {summary.recentIncidents.length === 0 ? (
                <p className="text-sm text-gray-400 bg-white rounded-xl border border-gray-200 p-4">
                  No active incidents.
                </p>
              ) : (
                summary.recentIncidents.map((i) => <IncidentRow key={i.id} incident={i} />)
              )}
            </div>
          </section>

          <section>
            <h2 className="text-sm font-semibold text-gray-700 uppercase tracking-wide mb-3">
              Recent Remediations
            </h2>
            <div className="space-y-2">
              {summary.recentRemediations.length === 0 ? (
                <p className="text-sm text-gray-400 bg-white rounded-xl border border-gray-200 p-4">
                  No remediation activity yet.
                </p>
              ) : (
                summary.recentRemediations.map((r) => <RemediationRow key={r.id} action={r} />)
              )}
            </div>
          </section>

          <section>
            <div className="flex items-center justify-between mb-3">
              <h2 className="text-sm font-semibold text-gray-700 uppercase tracking-wide">
                Cloud Accounts
              </h2>
              <a href="/tenants" className="text-xs font-medium text-azure-600 hover:text-azure-700">
                Manage in Clouds →
              </a>
            </div>
            <div className="space-y-2">
              {summary.cloudAccounts.map((a) => (
                <CloudAccountRow key={a.accountId} account={a} />
              ))}
              {summary.cloudAccounts.length === 0 && (
                <a
                  href="/tenants"
                  className="block w-full bg-white rounded-xl border border-dashed border-gray-300 p-3 text-sm text-gray-500 hover:border-azure-300 hover:text-azure-600 transition-colors text-center"
                >
                  Connect Azure, AWS, or GCP on the Clouds page to extend monitoring.
                </a>
              )}
            </div>
          </section>
        </div>
      </div>
    </div>
  );
}

function KpiTile({ label, value, accent, href }: { label: string; value: number | string; accent?: string; href?: string }) {
  const inner = (
    <>
      <p className="text-xs text-gray-500">{label}</p>
      <p className={`text-2xl font-bold mt-1 ${accent || 'text-gray-900'}`}>{value}</p>
    </>
  );
  const cls = 'bg-white rounded-xl border border-gray-200 p-4 block';
  return href ? (
    <a href={href} className={`${cls} hover:border-azure-300 hover:shadow-sm transition-all`}>{inner}</a>
  ) : (
    <div className={cls}>{inner}</div>
  );
}

function AnomalyCard({
  anomaly,
  onAction,
}: {
  anomaly: Anomaly;
  onAction: (a: Anomaly, status: string) => Promise<void>;
}) {
  const [busy, setBusy] = useState(false);
  const colors = SEVERITY_COLORS[anomaly.severity.toLowerCase()] ?? SEVERITY_COLORS.info;

  const act = async (status: string) => {
    setBusy(true);
    try {
      await onAction(anomaly, status);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="bg-white rounded-xl border border-gray-200 p-4">
      <div className="flex items-start gap-3">
        <div className={`w-2.5 h-2.5 rounded-full mt-1.5 flex-shrink-0 ${colors.dot}`} />
        <div className="flex-1 min-w-0">
          <p className="font-medium text-gray-900">{anomaly.title}</p>
          <div className="flex flex-wrap items-center gap-2 mt-1.5">
            <span className={`text-xs font-medium uppercase ${colors.text}`}>{anomaly.severity}</span>
            <span className="text-xs px-2 py-0.5 rounded-full bg-gray-100 text-gray-600">
              {KIND_LABELS[anomaly.kind] ?? anomaly.kind}
            </span>
            <ProviderBadge provider={anomalyProvider(anomaly)} />
            {anomaly.score != null && (
              <span className="text-xs text-gray-500">score {anomaly.score.toFixed(1)}</span>
            )}
            {anomaly.incidentId != null && (
              <span className="text-xs text-azure-600 font-medium">Incident #{anomaly.incidentId}</span>
            )}
          </div>
          {anomaly.description && (
            <p className="text-sm text-gray-600 mt-2">{anomaly.description}</p>
          )}
          {anomaly.resourceId && (
            <p className="text-xs text-gray-400 font-mono mt-1 truncate" title={anomaly.resourceId}>
              {changeResourceLeafName(anomaly.resourceId)}
            </p>
          )}
          {anomaly.observedValue != null && anomaly.baselineMean != null && (
            <p className="text-xs text-gray-500 mt-1">
              Observed <span className="font-semibold">{formatNumber(anomaly.observedValue)}</span> vs baseline{' '}
              <span className="font-semibold">{formatNumber(anomaly.baselineMean)}</span>
            </p>
          )}
        </div>
        <div className="flex flex-col gap-1.5 flex-shrink-0">
          <button
            disabled={busy}
            onClick={() => act('Acknowledged')}
            className="text-xs px-3 py-1 rounded-lg border border-gray-300 text-gray-600 hover:bg-gray-50 disabled:opacity-50"
          >
            Acknowledge
          </button>
          <button
            disabled={busy}
            onClick={() => act('Resolved')}
            className="text-xs px-3 py-1 rounded-lg border border-green-300 text-green-700 hover:bg-green-50 disabled:opacity-50"
          >
            Resolve
          </button>
          <button
            disabled={busy}
            onClick={() => act('FalsePositive')}
            className="text-xs px-3 py-1 rounded-lg border border-gray-200 text-gray-400 hover:bg-gray-50 disabled:opacity-50"
          >
            False positive
          </button>
        </div>
      </div>
    </div>
  );
}

function IncidentRow({ incident }: { incident: Incident }) {
  const colors = SEVERITY_COLORS[incident.severity.toLowerCase()] ?? SEVERITY_COLORS.info;
  return (
    <div className="bg-white rounded-xl border border-gray-200 p-3">
      <div className="flex items-center gap-2">
        <div className={`w-2 h-2 rounded-full flex-shrink-0 ${colors.dot}`} />
        <p className="text-sm font-medium text-gray-900 truncate flex-1">{incident.title}</p>
        {incident.slaBreached && (
          <span className="text-[10px] font-bold text-red-600 bg-red-50 px-1.5 py-0.5 rounded">SLA</span>
        )}
      </div>
      <div className="flex items-center gap-2 mt-1 text-xs text-gray-500">
        <span>#{incident.id}</span>
        <span>·</span>
        <span>{incident.status}</span>
        <span>·</span>
        <span>{incident.anomalyCount} anomalies</span>
        <span>·</span>
        <span>{incident.remediationCount} actions</span>
      </div>
    </div>
  );
}

const STATUS_COLORS: Record<string, string> = {
  Succeeded: 'text-green-600',
  Failed: 'text-red-600',
  Executing: 'text-azure-600',
  PendingApproval: 'text-orange-600',
  Approved: 'text-azure-600',
  Rejected: 'text-gray-400',
  Proposed: 'text-gray-500',
  Expired: 'text-gray-400',
  Cancelled: 'text-gray-400',
};

function RemediationRow({ action }: { action: RemediationAction }) {
  return (
    <div className="bg-white rounded-xl border border-gray-200 p-3">
      <div className="flex items-center gap-2 min-w-0">
        <ProviderBadge provider={remediationProvider(action)} />
        <p className="text-sm font-medium text-gray-900 truncate">{action.title}</p>
      </div>
      <div className="flex items-center gap-2 mt-1 text-xs">
        <span className={`font-medium ${STATUS_COLORS[action.status] ?? 'text-gray-500'}`}>{action.status}</span>
        <span className="text-gray-400">·</span>
        <span className="text-gray-500">{action.playbookKey}</span>
        {action.approvalMode === 'auto' && (
          <span className="text-[10px] font-medium bg-azure-50 text-azure-600 px-1.5 py-0.5 rounded">AUTO</span>
        )}
      </div>
    </div>
  );
}

function CloudAccountRow({ account }: { account: CloudAccount }) {
  const statusColor =
    account.status === 'Connected'
      ? 'text-green-600'
      : account.status === 'Degraded'
        ? 'text-orange-600'
        : 'text-gray-400';
  return (
    <div className="bg-white rounded-xl border border-gray-200 p-3 flex items-center justify-between">
      <div className="flex items-center gap-2 min-w-0">
        <ProviderBadge provider={account.provider} />
        <span className="text-sm text-gray-700 truncate">{account.displayName}</span>
      </div>
      <span className={`text-xs font-medium ${statusColor}`}>{account.status}</span>
    </div>
  );
}

function formatNumber(n: number): string {
  return Number.isInteger(n) ? n.toLocaleString() : n.toFixed(1);
}
