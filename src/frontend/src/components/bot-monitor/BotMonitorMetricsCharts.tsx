'use client';

import React from 'react';
import type { BotMonitorHealthMetricsDto } from '../../lib/types';

const bg = '#131315';
const surface = '#232427';
const accent = '#46224d';
const text = '#e3dbd3';

function MetricBars({ title, items }: { title: string; items: { label: string; value: number; max?: number }[] }) {
  const peak = Math.max(1, ...items.map((i) => i.value), ...(items.map((i) => i.max ?? 0)));
  return (
    <div className="rounded-lg border p-4 mt-4" style={{ backgroundColor: bg, borderColor: surface }}>
      <p className="text-sm font-medium mb-3" style={{ color: text }}>
        {title}
      </p>
      <div className="space-y-2">
        {items.map((item) => {
          const width = `${Math.max(4, (item.value / peak) * 100)}%`;
          return (
            <div key={item.label} className="flex items-center gap-2">
              <span className="text-[10px] w-24 opacity-70 shrink-0" style={{ color: text }}>
                {item.label}
              </span>
              <div className="flex h-3 flex-1 overflow-hidden rounded" style={{ backgroundColor: surface }}>
                <div className="rounded" style={{ width, backgroundColor: accent, minWidth: 4 }} title={String(item.value)} />
              </div>
              <span className="text-[10px] w-8 text-right opacity-80" style={{ color: text }}>
                {item.value}
              </span>
            </div>
          );
        })}
      </div>
    </div>
  );
}

export function BotMonitorMetricsCharts({ metrics }: { metrics?: BotMonitorHealthMetricsDto | null }) {
  if (!metrics) return null;
  const r = metrics.reported;
  const d = metrics.derived;
  const attempted = r?.actions?.attempted ?? 0;
  const failed = r?.actions?.failed ?? d.actionErrorsReported;

  return (
    <>
      <MetricBars
        title="Derived signals (server)"
        items={[
          { label: 'Runs per day', value: d.runsPerDay },
          { label: 'Failure streak', value: d.consecutiveFailureStreak },
          { label: 'Lateness min', value: d.checkInLatenessMinutes },
          { label: 'Action errors', value: d.actionErrorsReported },
        ]}
      />
      <MetricBars
        title="Reported workload"
        items={[
          { label: 'Inbox open', value: r?.inbox?.unanswered ?? 0 },
          { label: 'Inbox oldest', value: r?.inbox?.oldestMinutes ?? 0 },
          { label: 'Approvals', value: r?.approvals?.pending ?? 0 },
          { label: 'Queue depth', value: r?.queue?.open ?? 0 },
          { label: 'Handoffs', value: r?.escalations ?? 0 },
        ]}
      />
      {attempted > 0 && (
        <MetricBars
          title="Actions (reported window)"
          items={[
            { label: 'Attempted', value: attempted },
            { label: 'Failed', value: failed },
          ]}
        />
      )}
    </>
  );
}
