'use client';

import React from 'react';
import useSWR from 'swr';
import { getPortalBots } from '../../../lib/api';
import { useTenantContext } from '../../../contexts/TenantContext';
import {
  BotMonitorShell,
  PresenceBadge,
  botMonitorTableClass,
  botMonitorTdStyle,
  botMonitorThStyle,
} from '../../../components/bot-monitor/BotMonitorShell';

export default function PortalBotsPage() {
  const { tenantId, currentOrg } = useTenantContext();
  const { data, error, isLoading } = useSWR(
    tenantId ? ['portal-bots', tenantId] : null,
    () => getPortalBots(tenantId!),
  );

  if (!tenantId) {
    return (
      <BotMonitorShell
        title="Your bots"
        subtitle="We scope this page to the organization you select in the sidebar."
      >
        <p className="opacity-80">Choose an organization to see bot health for that customer.</p>
      </BotMonitorShell>
    );
  }

  return (
    <BotMonitorShell
      title="Your bots"
      subtitle={`We show heartbeat health for ${currentOrg?.name ?? 'this organization'} only. Tenant context comes from your signed-in session, never from the URL.`}
    >
      {isLoading && <p className="opacity-70">Loading your bots...</p>}
      {error && <p className="text-red-300">We could not load bots for this organization.</p>}

      {!isLoading && !error && (
        <div className="space-y-8">
          {(data ?? []).length === 0 ? (
            <p className="opacity-70">No bots are linked to this organization yet.</p>
          ) : (
            (data ?? []).map((bot) => (
              <section
                key={bot.botId}
                className="rounded-lg border p-4"
                style={{ backgroundColor: '#232427', borderColor: '#46224d' }}
              >
                <div className="flex flex-wrap items-center justify-between gap-2 mb-3">
                  <div>
                    <h2 className="text-lg font-medium">{bot.displayName}</h2>
                    <p className="text-xs opacity-70 capitalize">{bot.platform.replace('_', ' ')}</p>
                  </div>
                  <PresenceBadge presence={bot.presence} />
                </div>
                <p className="text-xs opacity-80 mb-4">
                  Last seen: {bot.lastSeenAt ? new Date(bot.lastSeenAt).toLocaleString() : 'Never'}
                </p>
                <h3 className="text-sm font-medium mb-2" style={{ color: '#46224d' }}>
                  Weekly health (last 8 ISO weeks)
                </h3>
                <div className="overflow-x-auto">
                  <table className={botMonitorTableClass()}>
                    <thead>
                      <tr>
                        {['Week', 'Uptime %', 'Keep pass', 'Keep warn', 'Keep fail', 'Errors'].map((h) => (
                          <th key={h} className="px-2 py-1 text-left text-xs font-medium" style={botMonitorThStyle()}>
                            {h}
                          </th>
                        ))}
                      </tr>
                    </thead>
                    <tbody>
                      {bot.weeklyHistory.map((w) => (
                        <tr key={`${w.isoYear}-${w.isoWeek}`}>
                          <td className="px-2 py-1 text-xs" style={botMonitorTdStyle()}>
                            {w.isoYear}-W{String(w.isoWeek).padStart(2, '0')}
                          </td>
                          <td className="px-2 py-1 text-xs" style={botMonitorTdStyle()}>
                            {w.uptimePercent.toFixed(1)}
                          </td>
                          <td className="px-2 py-1 text-xs" style={botMonitorTdStyle()}>
                            {w.keepPassCount}
                          </td>
                          <td className="px-2 py-1 text-xs" style={botMonitorTdStyle()}>
                            {w.keepWarnCount}
                          </td>
                          <td className="px-2 py-1 text-xs" style={botMonitorTdStyle()}>
                            {w.keepFailCount}
                          </td>
                          <td className="px-2 py-1 text-xs" style={botMonitorTdStyle()}>
                            {w.errorCount}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </section>
            ))
          )}
        </div>
      )}
    </BotMonitorShell>
  );
}
