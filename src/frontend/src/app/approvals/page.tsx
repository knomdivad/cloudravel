'use client';

import React, { useState } from 'react';
import { useTenantContext } from '../../contexts/TenantContext';
import { useRemediations } from '../../lib/hooks';
import { approveRemediation, rejectRemediation } from '../../lib/api';
import type { RemediationAction } from '../../lib/types';

const RISK_COLORS: Record<string, { bg: string; text: string }> = {
  Low: { bg: 'bg-green-50', text: 'text-green-700' },
  Medium: { bg: 'bg-yellow-50', text: 'text-yellow-700' },
  High: { bg: 'bg-red-50', text: 'text-red-700' },
};

const PROVIDER_BADGES: Record<string, string> = {
  Azure: 'bg-azure-100 text-azure-700',
  Aws: 'bg-amber-100 text-amber-800',
  Gcp: 'bg-emerald-100 text-emerald-700',
};

const HISTORY_STATUS_COLORS: Record<string, string> = {
  Succeeded: 'bg-green-50 text-green-700',
  Failed: 'bg-red-50 text-red-700',
  Rejected: 'bg-gray-100 text-gray-500',
  Expired: 'bg-gray-100 text-gray-400',
  Executing: 'bg-azure-50 text-azure-700',
  Approved: 'bg-azure-50 text-azure-700',
};

export default function ApprovalsPage() {
  const { tenantId } = useTenantContext();
  const [tab, setTab] = useState<'pending' | 'history'>('pending');

  const { data: pendingData, isLoading, mutate: mutatePending } = useRemediations(tenantId, {
    status: 'PendingApproval',
    limit: 100,
  });
  const { data: allData, mutate: mutateAll } = useRemediations(tenantId, { limit: 100 });

  if (!tenantId) {
    return <div className="text-center py-20 text-gray-500">Select a tenant to view approvals.</div>;
  }

  const pending = pendingData?.actions ?? [];
  const history = (allData?.actions ?? []).filter((a) => a.status !== 'PendingApproval');

  const refresh = () => Promise.all([mutatePending(), mutateAll()]);

  return (
    <div className="space-y-5">
      <div>
        <h1 className="text-xl font-bold">Remediation Approvals</h1>
        <p className="text-sm text-gray-500 mt-1">
          Gated actions from Azure, AWS, and GCP (AIOps drift, multi-cloud governance sync, AI assistant, or operators).
          Nothing runs without approval unless tenant policy auto-approves low-risk playbooks.
        </p>
      </div>

      {/* Tabs */}
      <div className="flex gap-1 border-b border-gray-200">
        <TabButton active={tab === 'pending'} onClick={() => setTab('pending')}>
          Pending{pending.length > 0 ? ` (${pending.length})` : ''}
        </TabButton>
        <TabButton active={tab === 'history'} onClick={() => setTab('history')}>
          History
        </TabButton>
      </div>

      {tab === 'pending' ? (
        <div className="space-y-3">
          {isLoading ? (
            <EmptyState>Loading approval queue...</EmptyState>
          ) : pending.length === 0 ? (
            <EmptyState>
              Nothing awaiting approval. Proposed actions will appear here for review. ✓
            </EmptyState>
          ) : (
            pending.map((a) => (
              <ApprovalCard key={a.id} action={a} tenantId={tenantId} onDone={refresh} />
            ))
          )}
        </div>
      ) : (
        <div className="space-y-3">
          {history.length === 0 ? (
            <EmptyState>No remediation history yet.</EmptyState>
          ) : (
            history.map((a) => <HistoryCard key={a.id} action={a} />)
          )}
        </div>
      )}
    </div>
  );
}

function TabButton({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }) {
  return (
    <button
      onClick={onClick}
      className={`px-4 py-2 text-sm font-medium border-b-2 -mb-px transition-colors ${
        active
          ? 'border-azure-600 text-azure-700'
          : 'border-transparent text-gray-500 hover:text-gray-700'
      }`}
    >
      {children}
    </button>
  );
}

function EmptyState({ children }: { children: React.ReactNode }) {
  return (
    <div className="bg-white rounded-xl border border-gray-200 p-12 text-center text-gray-400">
      {children}
    </div>
  );
}

function ApprovalCard({
  action,
  tenantId,
  onDone,
}: {
  action: RemediationAction;
  tenantId: string;
  onDone: () => Promise<unknown>;
}) {
  const [busy, setBusy] = useState(false);
  const [rejecting, setRejecting] = useState(false);
  const [rejectReason, setRejectReason] = useState('');
  const [error, setError] = useState<string | null>(null);
  const risk = RISK_COLORS[action.riskLevel] ?? RISK_COLORS.Medium;

  const approve = async () => {
    setBusy(true);
    setError(null);
    try {
      await approveRemediation(tenantId, action.id);
      await onDone();
    } catch (e: any) {
      setError(e.message ?? 'Approval failed');
    } finally {
      setBusy(false);
    }
  };

  const reject = async () => {
    setBusy(true);
    setError(null);
    try {
      await rejectRemediation(tenantId, action.id, rejectReason || undefined);
      await onDone();
    } catch (e: any) {
      setError(e.message ?? 'Rejection failed');
    } finally {
      setBusy(false);
      setRejecting(false);
    }
  };

  return (
    <div className="bg-white rounded-xl border border-gray-200 p-5">
      <div className="flex items-start justify-between gap-4">
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <p className="font-semibold text-gray-900">{action.title}</p>
            <span className={`text-xs font-medium px-2 py-0.5 rounded-full ${risk.bg} ${risk.text}`}>
              {action.riskLevel} risk
            </span>
            <span className={`text-xs px-2 py-0.5 rounded-full font-medium ${PROVIDER_BADGES[action.provider] ?? ''}`}>
              {action.provider.toUpperCase()}
            </span>
          </div>

          <div className="mt-3 space-y-2 text-sm">
            <div>
              <p className="text-xs font-medium text-gray-500">Why this was proposed</p>
              <p className="text-gray-700">{action.reason}</p>
            </div>
            <div className="grid grid-cols-1 md:grid-cols-2 gap-2">
              <div>
                <p className="text-xs font-medium text-gray-500">Playbook</p>
                <p className="text-gray-700 font-mono text-xs">{action.playbookKey}</p>
              </div>
              <div>
                <p className="text-xs font-medium text-gray-500">Requested by</p>
                <p className="text-gray-700">{action.requestedBy}</p>
              </div>
            </div>
            {action.resourceId && (
              <div>
                <p className="text-xs font-medium text-gray-500">Target resource</p>
                <p className="text-gray-700 font-mono text-xs break-all">{action.resourceId}</p>
              </div>
            )}
            {action.parametersJson && (
              <div>
                <p className="text-xs font-medium text-gray-500">Parameters</p>
                <pre className="text-xs bg-gray-50 rounded-lg p-2 overflow-x-auto">{prettyJson(action.parametersJson)}</pre>
              </div>
            )}
            <div className="flex items-center gap-4 text-xs text-gray-400">
              <span>Proposed {new Date(action.createdAt).toLocaleString()}</span>
              {action.expiresAt && <span>Expires {new Date(action.expiresAt).toLocaleString()}</span>}
              {action.incidentId != null && <span className="text-azure-600">Incident #{action.incidentId}</span>}
            </div>
          </div>
        </div>

        <div className="flex flex-col gap-2 flex-shrink-0 w-32">
          <button
            disabled={busy}
            onClick={approve}
            className="w-full bg-azure-600 text-white text-sm py-2 rounded-lg font-medium hover:bg-azure-700 disabled:opacity-50 transition-colors"
          >
            {busy ? 'Working...' : 'Approve & Run'}
          </button>
          <button
            disabled={busy}
            onClick={() => setRejecting(!rejecting)}
            className="w-full border border-gray-300 text-gray-600 text-sm py-2 rounded-lg font-medium hover:bg-gray-50 disabled:opacity-50 transition-colors"
          >
            Reject
          </button>
        </div>
      </div>

      {rejecting && (
        <div className="mt-3 flex gap-2">
          <input
            value={rejectReason}
            onChange={(e) => setRejectReason(e.target.value)}
            placeholder="Reason (optional)"
            className="flex-1 border border-gray-300 rounded-lg px-3 py-1.5 text-sm focus:border-azure-500 focus:outline-none"
          />
          <button
            disabled={busy}
            onClick={reject}
            className="bg-red-600 text-white text-sm px-4 py-1.5 rounded-lg font-medium hover:bg-red-700 disabled:opacity-50"
          >
            Confirm reject
          </button>
        </div>
      )}

      {error && <p className="mt-3 text-sm text-red-600">{error}</p>}
    </div>
  );
}

function HistoryCard({ action }: { action: RemediationAction }) {
  const [expanded, setExpanded] = useState(false);
  const statusCls = HISTORY_STATUS_COLORS[action.status] ?? 'bg-gray-100 text-gray-500';

  return (
    <div className="bg-white rounded-xl border border-gray-200">
      <button onClick={() => setExpanded(!expanded)} className="w-full text-left px-5 py-3 flex items-center gap-3">
        <span className={`text-xs font-medium px-2 py-0.5 rounded-full ${statusCls}`}>{action.status}</span>
        <span className="text-sm font-medium text-gray-900 truncate flex-1">{action.title}</span>
        {action.approvalMode === 'auto' && (
          <span className="text-[10px] font-medium bg-azure-50 text-azure-600 px-1.5 py-0.5 rounded">AUTO</span>
        )}
        <span className="text-xs text-gray-400 whitespace-nowrap">
          {new Date(action.completedAt ?? action.createdAt).toLocaleString()}
        </span>
      </button>
      {expanded && (
        <div className="px-5 pb-4 border-t border-gray-100 pt-3 space-y-2 text-sm">
          <p className="text-gray-700">{action.reason}</p>
          {action.approvedBy && (
            <p className="text-xs text-gray-500">Approved by {action.approvedBy}</p>
          )}
          {action.rejectedReason && (
            <p className="text-xs text-gray-500">Rejected: {action.rejectedReason}</p>
          )}
          {action.errorMessage && (
            <p className="text-xs text-red-600 font-mono">{action.errorMessage}</p>
          )}
          {action.resultJson && (
            <pre className="text-xs bg-gray-50 rounded-lg p-2 overflow-x-auto">{prettyJson(action.resultJson)}</pre>
          )}
        </div>
      )}
    </div>
  );
}

function prettyJson(raw: string): string {
  try {
    return JSON.stringify(JSON.parse(raw), null, 2);
  } catch {
    return raw;
  }
}
