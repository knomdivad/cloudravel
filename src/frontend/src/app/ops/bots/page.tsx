'use client';

import React, { useEffect, useMemo, useState } from 'react';
import useSWR from 'swr';
import { getOpsBots } from '../../../lib/api';
import {
  BotMonitorShell,
  PresenceBadge,
  KeepBadge,
  botMonitorTableClass,
  botMonitorTdStyle,
  botMonitorThStyle,
} from '../../../components/bot-monitor/BotMonitorShell';
import { useAuth } from '../../../contexts/AuthContext';

export default function OpsBotsPage() {
  const { isSystemAdmin } = useAuth();
  const [tenantFilter, setTenantFilter] = useState('');
  const { data, error, isLoading, mutate } = useSWR(
    isSystemAdmin ? ['ops-bots', tenantFilter] : null,
    () => getOpsBots(tenantFilter || undefined),
  );

  const tenants = useMemo(() => {
    const names = new Map<string, string>();
    (data ?? []).forEach((b) => names.set(b.tenantId, b.tenantName));
    return Array.from(names.entries()).sort((a, b) => a[1].localeCompare(b[1]));
  }, [data]);

  useEffect(() => {
    if (!isSystemAdmin) return;
    mutate();
  }, [tenantFilter, isSystemAdmin, mutate]);

  if (!isSystemAdmin) {
    return (
      <BotMonitorShell
        title="Bot fleet"
        subtitle="We restrict this view to platform operators."
      >
        <p className="opacity-80">Sign in with a system administrator account to review all customer bots.</p>
      </BotMonitorShell>
    );
  }

  return (
    <BotMonitorShell
      title="Bot fleet"
      subtitle="We monitor heartbeat health across every BotMonitor tenant. Filter by tenant to focus the table."
    >
      <div className="mb-4 flex flex-wrap items-center gap-3">
        <label className="text-sm opacity-80" htmlFor="tenant-filter">
          Tenant
        </label>
        <select
          id="tenant-filter"
          className="rounded-md px-3 py-2 text-sm"
          style={{ backgroundColor: '#232427', color: '#e3dbd3', border: '1px solid #46224d' }}
          value={tenantFilter}
          onChange={(e) => setTenantFilter(e.target.value)}
        >
          <option value="">All tenants</option>
          {tenants.map(([id, name]) => (
            <option key={id} value={id}>
              {name}
            </option>
          ))}
        </select>
      </div>

      {isLoading && <p className="opacity-70">Loading bots...</p>}
      {error && <p className="text-red-300">We could not load the fleet list. Try again shortly.</p>}

      {!isLoading && !error && (
        <div className="overflow-x-auto">
          <table className={botMonitorTableClass()}>
            <thead>
              <tr>
                {['Tenant', 'Bot', 'Platform', 'Presence', 'Last seen', 'Last error', 'Keep'].map((h) => (
                  <th key={h} className="px-3 py-2 text-left font-medium" style={botMonitorThStyle()}>
                    {h}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {(data ?? []).length === 0 ? (
                <tr>
                  <td colSpan={7} className="px-3 py-8 text-center opacity-70" style={botMonitorTdStyle()}>
                    No bots registered yet. Seed demo data or create bots through the registry API.
                  </td>
                </tr>
              ) : (
                (data ?? []).map((bot) => (
                  <tr key={bot.botId}>
                    <td className="px-3 py-2" style={botMonitorTdStyle()}>
                      {bot.tenantName}
                    </td>
                    <td className="px-3 py-2" style={botMonitorTdStyle()}>
                      {bot.displayName}
                      {bot.revokedAt ? <span className="ml-2 text-xs opacity-60">(revoked)</span> : null}
                    </td>
                    <td className="px-3 py-2 capitalize" style={botMonitorTdStyle()}>
                      {bot.platform.replace('_', ' ')}
                    </td>
                    <td className="px-3 py-2" style={botMonitorTdStyle()}>
                      <PresenceBadge presence={bot.presence} />
                    </td>
                    <td className="px-3 py-2 text-xs" style={botMonitorTdStyle()}>
                      {bot.lastSeenAt ? new Date(bot.lastSeenAt).toLocaleString() : 'Never'}
                    </td>
                    <td className="px-3 py-2 text-xs max-w-xs truncate" style={botMonitorTdStyle()}>
                      {bot.lastError ?? 'n/a'}
                    </td>
                    <td className="px-3 py-2" style={botMonitorTdStyle()}>
                      <KeepBadge value={bot.lastKeepCheck} />
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>
      )}
    </BotMonitorShell>
  );
}
