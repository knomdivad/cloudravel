'use client';

import React from 'react';

const typePalette: Record<string, { bg: string; border: string; text: string }> = {
  grokbot: { bg: '#2a1f33', border: '#7c4dff', text: '#e8deff' },
  chatgpt: { bg: '#1a2a24', border: '#10a37f', text: '#d4f5ea' },
  muse_meta: { bg: '#1f2433', border: '#0866ff', text: '#dbe8ff' },
  openclaw: { bg: '#2a2218', border: '#c77800', text: '#ffe8c2' },
  hermes: { bg: '#1a2830', border: '#00bcd4', text: '#ccf7ff' },
  dot: { bg: '#252525', border: '#e3dbd3', text: '#f5f0ea' },
  other_plugin: { bg: '#232427', border: '#46224d', text: '#e3dbd3' },
};

export function botTypePalette(typeKey: string) {
  return typePalette[typeKey] ?? typePalette.other_plugin;
}

export function BotTypeBadge({ typeKey, displayName }: { typeKey: string; displayName: string }) {
  const palette = botTypePalette(typeKey);
  return (
    <span
      className="inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium"
      style={{
        backgroundColor: palette.bg,
        color: palette.text,
        border: `1px solid ${palette.border}`,
      }}
    >
      {displayName}
    </span>
  );
}

export function BotTypeCountChips({
  counts,
}: {
  counts: { typeKey: string; displayName: string; count: number }[];
}) {
  if (!counts.length) return null;
  return (
    <div className="flex flex-wrap gap-2 mt-2">
      {counts.map((c) => (
        <span key={c.typeKey} className="inline-flex items-center gap-1.5 text-xs">
          <BotTypeBadge typeKey={c.typeKey} displayName={c.displayName} />
          <span className="opacity-80">{c.count}</span>
        </span>
      ))}
    </div>
  );
}
