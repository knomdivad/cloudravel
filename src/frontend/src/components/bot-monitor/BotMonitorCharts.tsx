'use client';

import React from 'react';

const bg = '#131315';
const surface = '#232427';
const accent = '#46224d';
const text = '#e3dbd3';

export function StatusTiles({
  total,
  online,
  stale,
  offline,
  avgUptime,
}: {
  total: number;
  online: number;
  stale: number;
  offline: number;
  avgUptime: number;
}) {
  const tiles = [
    { label: 'Total bots', value: String(total) },
    { label: 'Online', value: String(online) },
    { label: 'Stale', value: String(stale) },
    { label: 'Offline', value: String(offline) },
    { label: 'Avg uptime', value: `${avgUptime.toFixed(1)}%` },
  ];
  return (
    <div className="grid grid-cols-2 md:grid-cols-5 gap-3 mb-6">
      {tiles.map((t) => (
        <div
          key={t.label}
          className="rounded-lg border px-4 py-3"
          style={{ backgroundColor: surface, borderColor: accent, color: text }}
        >
          <p className="text-xs opacity-70">{t.label}</p>
          <p className="text-xl font-semibold mt-1">{t.value}</p>
        </div>
      ))}
    </div>
  );
}

export function UptimeTrendChart({ history }: { history: { isoYear: number; isoWeek: number; uptimePercent: number }[] }) {
  const max = Math.max(100, ...history.map((h) => h.uptimePercent), 1);
  return (
    <div className="rounded-lg border p-4" style={{ backgroundColor: bg, borderColor: surface }}>
      <p className="text-sm font-medium mb-3" style={{ color: text }}>
        Uptime trend (ISO weeks)
      </p>
      <div className="flex items-end gap-2 h-32">
        {history.map((h) => {
          const height = `${Math.max(4, (h.uptimePercent / max) * 100)}%`;
          return (
            <div key={`${h.isoYear}-${h.isoWeek}`} className="flex-1 flex flex-col items-center gap-1">
              <div className="w-full rounded-t" style={{ height, backgroundColor: accent, minHeight: 4 }} title={`${h.uptimePercent}%`} />
              <span className="text-[10px] opacity-70" style={{ color: text }}>
                W{h.isoWeek}
              </span>
            </div>
          );
        })}
      </div>
    </div>
  );
}

export function CheckinSparkline({ points }: { points: { receivedAt: string; status: string }[] }) {
  return (
    <div className="rounded-lg border p-4 mt-4" style={{ backgroundColor: bg, borderColor: surface }}>
      <p className="text-sm font-medium mb-3" style={{ color: text }}>
        Check in history (48h)
      </p>
      <div className="flex flex-wrap gap-1">
        {points.length === 0 ? (
          <span className="text-xs opacity-60" style={{ color: text }}>
            No check ins in window
          </span>
        ) : (
          points.map((p) => (
            <span
              key={p.receivedAt}
              className="inline-block h-3 w-3 rounded-sm"
              style={{ backgroundColor: p.status === 'ok' ? accent : surface, border: `1px solid ${accent}` }}
              title={`${new Date(p.receivedAt).toLocaleString()} ${p.status}`}
            />
          ))
        )}
      </div>
    </div>
  );
}

export function HealthTrendChart({
  buckets,
}: {
  buckets: { keepPassCount: number; keepWarnCount: number; keepFailCount: number; errorCount: number; isoWeek: number }[];
}) {
  return (
    <div className="rounded-lg border p-4 mt-4" style={{ backgroundColor: bg, borderColor: surface }}>
      <p className="text-sm font-medium mb-3" style={{ color: text }}>
        Health trend (Keep and errors)
      </p>
      <div className="space-y-2">
        {buckets.map((b) => {
          const total = b.keepPassCount + b.keepWarnCount + b.keepFailCount + b.errorCount || 1;
          const passW = (b.keepPassCount / total) * 100;
          const warnW = (b.keepWarnCount / total) * 100;
          const failW = (b.keepFailCount / total) * 100;
          const errW = (b.errorCount / total) * 100;
          return (
            <div key={b.isoWeek} className="flex items-center gap-2">
              <span className="text-[10px] w-8 opacity-70" style={{ color: text }}>
                W{b.isoWeek}
              </span>
              <div className="flex h-3 flex-1 overflow-hidden rounded" style={{ backgroundColor: surface }}>
                <div style={{ width: `${passW}%`, backgroundColor: accent }} />
                <div style={{ width: `${warnW}%`, backgroundColor: text, opacity: 0.5 }} />
                <div style={{ width: `${failW}%`, backgroundColor: surface, borderLeft: `1px solid ${accent}` }} />
                <div style={{ width: `${errW}%`, backgroundColor: bg, borderLeft: `1px solid ${text}` }} />
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
}
