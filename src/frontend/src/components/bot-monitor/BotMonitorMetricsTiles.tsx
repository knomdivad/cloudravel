'use client';

import React from 'react';
import type { BotMonitorHealthMetricsDto } from '../../lib/types';

const surface = '#232427';
const accent = '#46224d';
const text = '#e3dbd3';
const bg = '#131315';

function Tile({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-lg border px-3 py-2" style={{ backgroundColor: surface, borderColor: accent, color: text }}>
      <p className="text-[10px] uppercase tracking-wide opacity-70">{label}</p>
      <p className="text-sm font-semibold mt-1">{value}</p>
    </div>
  );
}

export function BotMonitorMetricsTiles({ metrics }: { metrics?: BotMonitorHealthMetricsDto | null }) {
  if (!metrics) return null;
  const r = metrics.reported;
  const d = metrics.derived;
  const badConnectors = r?.connectors?.filter((c) => c.state !== 'ok').length ?? 0;
  return (
    <div className="mt-4 space-y-3">
      <p className="text-xs font-medium opacity-80">Health metrics</p>
      <div className="grid grid-cols-2 md:grid-cols-4 gap-2">
        <Tile label="Failure streak" value={String(d.consecutiveFailureStreak)} />
        <Tile label="Routine missed" value={d.routineMissed ? 'yes' : 'no'} />
        <Tile label="Check in lateness" value={`${d.checkInLatenessMinutes} min`} />
        <Tile label="Runs per day" value={String(d.runsPerDay)} />
        <Tile label="Action errors" value={String(d.actionErrorsReported)} />
        <Tile label="Inbox unanswered" value={String(r?.inbox?.unanswered ?? 0)} />
        <Tile label="Inbox oldest" value={`${r?.inbox?.oldestMinutes ?? 0} min`} />
        <Tile label="Approvals pending" value={String(r?.approvals?.pending ?? 0)} />
        <Tile label="Approval oldest" value={`${r?.approvals?.oldestMinutes ?? 0} min`} />
        <Tile label="Queue depth" value={String(r?.queue?.open ?? 0)} />
        <Tile label="Queue oldest" value={`${r?.queue?.oldestMinutes ?? 0} min`} />
        <Tile label="Handoffs" value={String(r?.escalations ?? 0)} />
        <Tile label="Connector issues" value={String(badConnectors)} />
      </div>
      {r?.connectors && r.connectors.length > 0 && (
        <div className="rounded-lg border p-3" style={{ backgroundColor: bg, borderColor: surface }}>
          <p className="text-xs opacity-70 mb-2">Connector login health</p>
          <ul className="text-xs space-y-1">
            {r.connectors.map((c) => (
              <li key={c.name}>
                {c.name}: {c.state}
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  );
}
