'use client';

import React from 'react';

const palette = {
  bg: '#131315',
  surface: '#232427',
  accent: '#46224d',
  text: '#e3dbd3',
};

const typeIcons: Record<string, string> = {
  grokbot: '\u2726',
  chatgpt: '\u25C6',
  muse_meta: '\u25CE',
  openclaw: '\u2736',
  hermes: '\u27E1',
  dot: '\u2022',
  other_plugin: '\u25AA',
};

function tintForType(typeKey: string): string {
  const hash = typeKey.split('').reduce((acc, c) => acc + c.charCodeAt(0), 0);
  const mix = hash % 3;
  if (mix === 0) return palette.surface;
  if (mix === 1) return palette.accent;
  return palette.bg;
}

export function botTypePalette(_typeKey: string) {
  return { bg: palette.bg, border: palette.accent, text: palette.text };
}

export function BotTypeBadge({ typeKey, displayName }: { typeKey: string; displayName: string }) {
  const icon = typeIcons[typeKey] ?? typeIcons.other_plugin;
  const bg = tintForType(typeKey);
  return (
    <span
      className="inline-flex items-center gap-1 rounded-full px-2.5 py-0.5 text-xs font-medium"
      style={{
        backgroundColor: bg,
        color: palette.text,
        border: `1px solid ${palette.accent}`,
      }}
    >
      <span aria-hidden style={{ color: palette.text, opacity: 0.85 }}>
        {icon}
      </span>
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
          <span style={{ color: palette.text, opacity: 0.8 }}>{c.count}</span>
        </span>
      ))}
    </div>
  );
}
