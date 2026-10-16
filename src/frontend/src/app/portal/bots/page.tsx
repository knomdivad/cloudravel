'use client';

import React from 'react';
import useSWR from 'swr';
import { getPortalBotDashboard } from '../../../lib/api';
import { useTenantContext } from '../../../contexts/TenantContext';
import {
  BotMonitorShell,
  PresenceBadge,
} from '../../../components/bot-monitor/BotMonitorShell';
import {
  CheckinSparkline,
  HealthTrendChart,
  StatusTiles,
  UptimeTrendChart,
} from '../../../components/bot-monitor/BotMonitorCharts';
import { BotMonitorMetricsTiles } from '../../../components/bot-monitor/BotMonitorMetricsTiles';
import { BotMonitorMetricsCharts } from '../../../components/bot-monitor/BotMonitorMetricsCharts';

export default function PortalBotsPage() {
  const { tenantId, currentOrg } = useTenantContext();
  const { data, error, isLoading } = useSWR(
    tenantId ? ['portal-bot-dashboard', tenantId] : null,
    () => getPortalBotDashboard(tenantId!),
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
      subtitle={`Heartbeat health for ${currentOrg?.name ?? 'this organization'}. Data is scoped to your user and organization from the signed in session.`}
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

          {dashboard.bots.length === 0 ? (
            <p className="opacity-70">No bots are assigned to your user in this organization yet.</p>
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
                      <h2 className="text-lg font-medium">{bot.displayName}</h2>
                      <p className="text-xs opacity-70 capitalize">{bot.platform.replace('_', ' ')}</p>
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
