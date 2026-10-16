'use client';

import React from 'react';

const bg = '#131315';
const surface = '#232427';
const accent = '#46224d';
const text = '#e3dbd3';

export function BotMonitorShell({
  title,
  subtitle,
  children,
}: {
  title: string;
  subtitle: string;
  children: React.ReactNode;
}) {
  return (
    <div className="min-h-full rounded-xl border p-6" style={{ backgroundColor: bg, borderColor: surface, color: text }}>
      <header className="mb-6">
        <h1 className="text-2xl font-semibold tracking-tight">{title}</h1>
        <p className="mt-2 text-sm opacity-80">{subtitle}</p>
      </header>
      {children}
    </div>
  );
}

export function PresenceBadge({ presence }: { presence: string }) {
  const key = presence.toLowerCase();
  const tone =
    key === 'online'
      ? accent
      : key === 'stale'
        ? '#8a6a3a'
        : '#5c5c5c';
  return (
    <span
      className="inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium capitalize"
      style={{ backgroundColor: surface, color: text, border: `1px solid ${tone}` }}
    >
      {presence}
    </span>
  );
}

export function KeepBadge({ value }: { value?: string | null }) {
  if (!value || value === 'none') return <span className="opacity-50 text-xs">Keep: n/a</span>;
  return (
    <span className="text-xs capitalize" style={{ color: accent }}>
      Keep: {value}
    </span>
  );
}

export function botMonitorTableClass() {
  return 'w-full text-sm border-collapse';
}

export function botMonitorThStyle(): React.CSSProperties {
  return { backgroundColor: surface, color: text, borderBottom: `1px solid ${accent}` };
}

export function botMonitorTdStyle(): React.CSSProperties {
  return { borderBottom: `1px solid ${surface}`, color: text };
}
