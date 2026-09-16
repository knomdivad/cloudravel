'use client';

import { useState } from 'react';
import { api } from '@/lib/api';
import type { AssessmentState, AssessmentReport } from '@/lib/api';
import { useAssessment, useAssessmentReport } from '@/lib/hooks';
import { useTenantContext } from '@/contexts/TenantContext';

const TIER_LABELS: Record<number, string> = {
  1: 'Quantified savings',
  2: 'Unquantified cost',
  3: 'Risk',
};

const TIER_STYLES: Record<number, string> = {
  1: 'bg-green-100 text-green-800',
  2: 'bg-amber-100 text-amber-800',
  3: 'bg-red-100 text-red-800',
};

const fmtDate = (iso?: string | null) =>
  iso ? new Date(iso).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' }) : '—';

const fmtMoney = (n: number) =>
  n.toLocaleString(undefined, { style: 'currency', currency: 'USD', maximumFractionDigits: 0 });

export default function AssessmentPage() {
  const { tenantId, canManageClouds } = useTenantContext();
  const { data: state, error: stateError, mutate: mutateState } = useAssessment(tenantId);
  const { data: report, error: reportError, mutate: mutateReport } = useAssessmentReport(tenantId);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [showStartModal, setShowStartModal] = useState(false);
  const [windowDays, setWindowDays] = useState(10);
  const [fee, setFee] = useState(3000);

  const run = async (label: string, fn: () => Promise<unknown>) => {
    setBusy(label);
    setError(null);
    try {
      await fn();
      await Promise.all([mutateState(), mutateReport()]);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Request failed.');
    } finally {
      setBusy(null);
    }
  };

  const download = (kind: 'csv' | 'md') => {
    if (!tenantId) return;
    const path = kind === 'csv'
      ? `/api/assessment/report?format=csv`
      : `/api/assessment/report?format=md`;
    window.open(path, '_blank');
  };

  if (!tenantId) {
    return <div className="text-center py-20 text-gray-500">Select an organization to manage its assessment.</div>;
  }

  const stateBadge = (s: AssessmentState) => {
    const styles: Record<string, string> = {
      watching: 'bg-blue-100 text-blue-800',
      completed: 'bg-purple-100 text-purple-800',
      not_started: 'bg-gray-100 text-gray-800',
      standard: 'bg-green-100 text-green-800',
    };
    const labels: Record<string, string> = {
      watching: 'Watching estate',
      completed: 'Assessment complete',
      not_started: 'Not started',
      standard: 'Ongoing monitoring',
    };
    return <span className={`px-2 py-0.5 rounded text-xs font-medium ${styles[s.state] || 'bg-gray-100 text-gray-800'}`}>{labels[s.state] || s.state}</span>;
  };

  return (
    <div className="space-y-5">
      <div className="flex items-start justify-between">
        <div>
          <h1 className="text-xl font-bold">Assessment</h1>
          <p className="text-sm text-gray-500 mt-1">
            Time-boxed, read-only cost assessment: snapshot the estate, watch changes for the window,
            deliver the fix-list ranked by dollars. Remediation is locked while an assessment runs.
          </p>
        </div>
        {canManageClouds && state && (
          <div className="flex gap-2">
            {state.state === 'standard' && (
              <button
                onClick={() => setShowStartModal(true)}
                disabled={busy !== null}
                className="px-3 py-1.5 text-sm font-medium text-white bg-azure-600 hover:bg-azure-700 rounded-lg disabled:opacity-50"
              >
                Start assessment
              </button>
            )}
            {state.state === 'watching' && (
              <button
                onClick={() => run('complete', () => api.completeAssessment(tenantId))}
                disabled={busy !== null}
                className="px-3 py-1.5 text-sm font-medium text-white bg-purple-600 hover:bg-purple-700 rounded-lg disabled:opacity-50"
              >
                {busy === 'complete' ? 'Completing…' : 'Complete assessment'}
              </button>
            )}
            {state.state === 'completed' && (
              <button
                onClick={() => run('convert', () => api.convertAssessment(tenantId))}
                disabled={busy !== null}
                className="px-3 py-1.5 text-sm font-medium text-white bg-green-600 hover:bg-green-700 rounded-lg disabled:opacity-50"
              >
                {busy === 'convert' ? 'Converting…' : 'Convert to ongoing monitoring'}
              </button>
            )}
          </div>
        )}
      </div>

      {(error || stateError) && (
        <div className="bg-red-50 border border-red-200 text-red-700 rounded-lg p-3 text-sm">
          {error || String(stateError)}
        </div>
      )}

      {/* Engagement state */}
      {state && (
        <div className="bg-white rounded-xl border border-gray-200 p-4">
          <div className="flex items-center gap-3">
            <span className="font-medium">{state.workspaceName}</span>
            {stateBadge(state)}
            {state.readOnly && (
              <span className="px-2 py-0.5 rounded text-xs font-medium bg-red-100 text-red-700">
                Read-only — remediation locked
              </span>
            )}
          </div>
          <div className="grid grid-cols-2 md:grid-cols-4 gap-3 mt-3 text-sm">
            <div>
              <p className="text-gray-400 text-xs uppercase">Started</p>
              <p>{fmtDate(state.assessmentStartedAt)}</p>
            </div>
            <div>
              <p className="text-gray-400 text-xs uppercase">Window ends</p>
              <p>{fmtDate(state.assessmentEndsAt)}</p>
            </div>
            <div>
              <p className="text-gray-400 text-xs uppercase">Completed</p>
              <p>{fmtDate(state.assessmentCompletedAt)}</p>
            </div>
            <div>
              <p className="text-gray-400 text-xs uppercase">Approval mode</p>
              <p className="capitalize">{state.autoRemediationMode}</p>
            </div>
          </div>
        </div>
      )}

      {/* Report */}
      {reportError ? (
        <div className="bg-white rounded-xl border border-gray-200 p-12 text-center text-red-500 text-sm">
          Failed to load the assessment report.
        </div>
      ) : !report ? (
        <div className="bg-white rounded-xl border border-gray-200 p-12 text-center text-gray-400">Loading report…</div>
      ) : (
        <>
          {/* Savings + guarantee */}
          <div className="grid grid-cols-1 md:grid-cols-3 gap-3">
            <div className="bg-white rounded-xl border border-gray-200 p-4">
              <p className="text-xs text-gray-400 uppercase mb-1">Identified savings (annual)</p>
              <p className="text-2xl font-bold text-green-700">{fmtMoney(report.savings.identifiedAnnual)}</p>
              <p className="text-xs text-gray-500 mt-1">{fmtMoney(report.savings.identifiedMonthly)} / month</p>
            </div>
            <div className={`rounded-xl border p-4 ${report.savings.meetsGuarantee ? 'bg-green-50 border-green-200' : 'bg-white border-gray-200'}`}>
              <p className="text-xs text-gray-400 uppercase mb-1">Risk-free guarantee</p>
              <p className="text-2xl font-bold">
                {report.savings.feeMultiple.toFixed(2)}× fee
              </p>
              <p className="text-xs text-gray-500 mt-1">
                target {fmtMoney(report.savings.targetSavings)} (2× {fmtMoney(report.savings.fee)} fee) —{' '}
                {report.savings.meetsGuarantee ? 'guarantee met' : 'not yet met'}
              </p>
            </div>
            <div className="bg-white rounded-xl border border-gray-200 p-4">
              <p className="text-xs text-gray-400 uppercase mb-1">Estate</p>
              <p className="text-2xl font-bold">{report.estate.resourceCount}</p>
              <p className="text-xs text-gray-500 mt-1">
                resources · {report.estate.changesInWindow} changes in window · last snapshot{' '}
                {fmtDate(report.estate.lastSnapshotAt)}
              </p>
            </div>
          </div>

          {/* Exports */}
          <div className="flex items-center gap-2">
            <span className="text-sm text-gray-500">Export fix-list:</span>
            <button
              onClick={() => download('md')}
              className="px-3 py-1.5 text-sm border border-gray-300 rounded-lg hover:bg-gray-50"
            >
              Markdown report
            </button>
            <button
              onClick={() => download('csv')}
              className="px-3 py-1.5 text-sm border border-gray-300 rounded-lg hover:bg-gray-50"
            >
              CSV
            </button>
            <span className="text-xs text-gray-400 ml-2">Generated {fmtDate(report.generatedAt)}</span>
          </div>

          {/* Fix list */}
          <div className="bg-white rounded-xl border border-gray-200">
            <div className="px-4 py-3 border-b border-gray-100">
              <h2 className="font-semibold">Fix-list ({report.fixList.length} items, ranked by dollars)</h2>
            </div>
            {report.fixList.length === 0 ? (
              <p className="p-8 text-center text-gray-400 text-sm">No findings recorded for this workspace yet.</p>
            ) : (
              <ul className="divide-y divide-gray-100">
                {report.fixList.map((item) => (
                  <li key={item.rank} className="px-4 py-3 flex items-start gap-3">
                    <span className="text-gray-400 font-mono text-sm w-6 text-right pt-0.5">{item.rank}</span>
                    <div className="flex-1 min-w-0">
                      <div className="flex items-center gap-2 flex-wrap">
                        <span className="font-medium text-sm">{item.title}</span>
                        <span className={`px-1.5 py-0.5 rounded text-[10px] font-medium uppercase ${TIER_STYLES[item.tier] || 'bg-gray-100 text-gray-800'}`}>
                          T{item.tier} · {TIER_LABELS[item.tier]}
                        </span>
                      </div>
                      {item.resourceId && (
                        <p className="text-xs text-gray-400 truncate mt-0.5" title={item.resourceId}>{item.resourceId}</p>
                      )}
                      {item.detail && <p className="text-sm text-gray-600 mt-1">{item.detail}</p>}
                      {item.remediation && (
                        <p className="text-sm text-gray-500 mt-0.5">
                          <span className="font-medium">Fix:</span> {item.remediation}
                        </p>
                      )}
                    </div>
                    <div className="text-right flex-shrink-0">
                      {item.estimatedAnnualSavings ? (
                        <p className="font-bold text-green-700">{fmtMoney(item.estimatedAnnualSavings)}/yr</p>
                      ) : (
                        <p className="text-gray-300 text-sm">—</p>
                      )}
                      <p className="text-[10px] text-gray-400 uppercase mt-0.5">{item.source} · {item.severity}</p>
                    </div>
                  </li>
                ))}
              </ul>
            )}
          </div>
        </>
      )}

      {/* Start modal */}
      {showStartModal && (
        <div className="fixed inset-0 bg-black/40 flex items-center justify-center z-50">
          <div className="bg-white rounded-xl shadow-2xl p-6 w-full max-w-md">
            <h2 className="font-bold text-lg mb-1">Start assessment</h2>
            <p className="text-sm text-gray-500 mb-4">
              Opens a read-only watch window on this workspace. Remediation approval is locked until the
              assessment is completed or converted.
            </p>
            <label className="block text-sm font-medium mb-1">Watch window (days)</label>
            <input
              type="number"
              min={1}
              max={90}
              value={windowDays}
              onChange={(e) => setWindowDays(Number(e.target.value) || 10)}
              className="w-full border border-gray-300 rounded-lg px-3 py-2 mb-3 focus:border-azure-500 focus:outline-none"
            />
            <label className="block text-sm font-medium mb-1">Fee (USD, for the guarantee math)</label>
            <input
              type="number"
              min={0}
              value={fee}
              onChange={(e) => setFee(Number(e.target.value) || 0)}
              className="w-full border border-gray-300 rounded-lg px-3 py-2 mb-4 focus:border-azure-500 focus:outline-none"
            />
            <div className="flex justify-end gap-2">
              <button
                onClick={() => setShowStartModal(false)}
                className="px-3 py-1.5 text-sm border border-gray-300 rounded-lg hover:bg-gray-50"
              >
                Cancel
              </button>
              <button
                onClick={() => {
                  setShowStartModal(false);
                  run('start', () => api.startAssessment(tenantId, { windowDays, fee }));
                }}
                disabled={busy !== null}
                className="px-3 py-1.5 text-sm font-medium text-white bg-azure-600 hover:bg-azure-700 rounded-lg disabled:opacity-50"
              >
                Start
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
