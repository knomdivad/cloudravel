'use client';

import React, { useState } from 'react';
import useSWR from 'swr';
import { getPortalBotDashboard, getPortalBotTypes } from '../../../lib/api';
import { useTenantContext } from '../../../contexts/TenantContext';
import {
  BotMonitorShell,
  PresenceBadge,
} from '../../../components/bot-monitor/BotMonitorShell';
import { BotTypeBadge, BotTypeCountChips } from '../../../components/bot-monitor/BotTypeBadge';
import {
  CheckinSparkline,
  HealthTrendChart,
  StatusTiles,
  UptimeTrendChart,
} from '../../../components/bot-monitor/BotMonitorCharts';
import { BotMonitorMetricsTiles } from '../../../components/bot-monitor/BotMonitorMetricsTiles';
import { BotMonitorMetricsCharts } from '../../../components/bot-monitor/BotMonitorMetricsCharts';

export default function PortalBotsPage() {
  const { tenantId, currentOrg, canViewAllTenantBots, isTenantAdmin } = useTenantContext();
  const [typeFilter, setTypeFilter] = useState('');
  const botTypes = useSWR(tenantId ? ['portal-bot-types', tenantId] : null, () => getPortalBotTypes(tenantId!));
  const { data, error, isLoading } = useSWR(
    tenantId ? ['portal-bot-dashboard', tenantId, typeFilter] : null,
    () => getPortalBotDashboard(tenantId!, typeFilter || undefined),
  );

  if (!tenantId) {
    return (
      <BotMonitorShell
        title="Bot monitoring"
        subtitle="We scope this dashboard to the organization you select in the sidebar."
      >
        <p className="opacity-80">Choose an organization to see bot health for that customer.</p>
      </BotMonitorShell>
    );
  }

  const dashboard = data;

  return (
    <BotMonitorShell
      title="Bot monitoring"
      subtitle={
        canViewAllTenantBots
          ? `Organization wide bot monitoring for ${currentOrg?.name ?? 'this workspace'}. ${isTenantAdmin ? 'You are signed in as a tenant administrator.' : 'You are signed in as an organization administrator.'}`
          : `Heartbeat health for ${currentOrg?.name ?? 'this organization'}. Data is scoped to your user and organization from the signed in session.`
      }
    >
      {isLoading && <p className="opacity-70">Loading dashboard...</p>}
      {error && <p style={{ color: '#e3dbd3', opacity: 0.9 }}>We could not load bot monitoring for this organization.</p>}

      {!isLoading && !error && dashboard && (
        <>
          <StatusTiles
            total={dashboard.totalBots}
            online={dashboard.onlineCount}
            stale={dashboard.staleCount}
            offline={dashboard.offlineCount}
            avgUptime={dashboard.averageUptimePercent}
          />

          <div className="mb-6 flex flex-wrap items-center gap-2">
            <label className="text-xs opacity-80" htmlFor="portal-bot-type-filter">
              Bot type
            </label>
            <select
              id="portal-bot-type-filter"
              className="rounded border px-2 py-1 text-sm"
              style={{ backgroundColor: '#232427', borderColor: '#46224d', color: '#e3dbd3' }}
              value={typeFilter}
              onChange={(e) => setTypeFilter(e.target.value)}
            >
              <option value="">All types</option>
              {(botTypes.data ?? []).map((t) => (
                <option key={t.typeKey} value={t.typeKey}>
                  {t.displayName}
                </option>
              ))}
            </select>
            {(dashboard.typeCounts?.length ?? 0) > 0 && <BotTypeCountChips counts={dashboard.typeCounts!} />}
          </div>

          {(dashboard.viewScope === 'organization' || canViewAllTenantBots) && (dashboard.userBotCounts?.length ?? 0) > 0 && (
            <div className="mb-6 grid gap-2 md:grid-cols-3">
              {dashboard.userBotCounts!.map((u) => (
                <div
                  key={u.userId}
                  className="rounded-lg border px-3 py-2"
                  style={{ backgroundColor: '#232427', borderColor: '#46224d', color: '#e3dbd3' }}
                >
                  <p className="text-sm font-medium">{u.displayName}</p>
                  <p className="text-xs opacity-70">{u.email}</p>
                  <p className="text-xs mt-1">{u.botCount} bots</p>
                  {u.typeCounts && u.typeCounts.length > 0 && <BotTypeCountChips counts={u.typeCounts} />}
                </div>
              ))}
            </div>
          )}

          {dashboard.bots.length === 0 ? (
            <p className="opacity-70">
              {canViewAllTenantBots
                ? 'No bots are registered in this organization yet.'
                : 'No bots are assigned to your user in this organization yet.'}
            </p>
          ) : (
            <div className="space-y-8">
              {dashboard.bots.map((bot) => (
                <section
                  key={bot.botId}
                  className="rounded-lg border p-4"
                  style={{ backgroundColor: '#232427', borderColor: '#46224d' }}
                >
                  <div className="flex flex-wrap items-center justify-between gap-2 mb-4">
                    <div>
                      <div className="flex flex-wrap items-center gap-2">
                        <h2 className="text-lg font-medium">{bot.displayName}</h2>
                        {bot.botTypeKey && (
                          <BotTypeBadge
                            typeKey={bot.botTypeKey}
                            displayName={bot.botTypeDisplayName ?? bot.botTypeKey}
                          />
                        )}
                      </div>
                      <p className="text-xs opacity-70 capitalize">{bot.platform.replace('_', ' ')}</p>
                      {bot.ownerDisplayName && (
                        <p className="text-xs opacity-70">Owner: {bot.ownerDisplayName}</p>
                      )}
                    </div>
                    <PresenceBadge presence={bot.presence} />
                  </div>
                  <p className="text-xs opacity-80 mb-4">
                    Last seen: {bot.lastSeenAt ? new Date(bot.lastSeenAt).toLocaleString() : 'Never'}
                  </p>
                  <UptimeTrendChart history={bot.weeklyHistory} />
                  <HealthTrendChart buckets={bot.weeklyHistory} />
                  <CheckinSparkline points={bot.recentCheckins ?? []} />
                  <BotMonitorMetricsTiles metrics={bot.healthMetrics} />
                  <BotMonitorMetricsCharts metrics={bot.healthMetrics} />
                </section>
              ))}
            </div>
          )}
        </>
      )}
    </BotMonitorShell>
  );
}
