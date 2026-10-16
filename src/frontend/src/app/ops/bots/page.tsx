'use client';

import React, { useState } from 'react';
import useSWR from 'swr';
import {
  getOpsBotMetrics,
  getOpsFleet,
  getOpsFleetUserBots,
  getOpsFleetUsers,
  getPortalBotTypes,
} from '../../../lib/api';
import type { BotMonitorFleetTenant, BotMonitorFleetUser } from '../../../lib/types';
import { BotTypeBadge, BotTypeCountChips } from '../../../components/bot-monitor/BotTypeBadge';
import { BotMonitorMetricsTiles } from '../../../components/bot-monitor/BotMonitorMetricsTiles';
import { BotMonitorMetricsCharts } from '../../../components/bot-monitor/BotMonitorMetricsCharts';
import {
  BotMonitorShell,
  PresenceBadge,
  KeepBadge,
  botMonitorTableClass,
  botMonitorTdStyle,
  botMonitorThStyle,
} from '../../../components/bot-monitor/BotMonitorShell';
import { useAuth } from '../../../contexts/AuthContext';

type Drill = { level: 'fleet' } | { level: 'customer'; tenant: BotMonitorFleetTenant } | { level: 'user'; tenant: BotMonitorFleetTenant; user: BotMonitorFleetUser };

export default function OpsBotsPage() {
  const { isSystemAdmin } = useAuth();
  const [drill, setDrill] = useState<Drill>({ level: 'fleet' });
  const [typeFilter, setTypeFilter] = useState<string>('');

  const fleet = useSWR(isSystemAdmin ? 'ops-fleet' : null, getOpsFleet);
  const tenantId = drill.level !== 'fleet' ? drill.tenant.tenantId : null;
  const users = useSWR(tenantId && drill.level !== 'fleet' ? ['ops-fleet-users', tenantId] : null, () =>
    getOpsFleetUsers(tenantId!),
  );
  const userId = drill.level === 'user' ? drill.user.userId : null;
  const botTypes = useSWR(tenantId ? ['ops-bot-types', tenantId] : null, () => getPortalBotTypes(tenantId!));
  const userBots = useSWR(
    tenantId && userId ? ['ops-fleet-user-bots', tenantId, userId, typeFilter] : null,
    () => getOpsFleetUserBots(tenantId!, userId!, typeFilter || undefined),
  );
  const userMetrics = useSWR(
    tenantId && userId ? ['ops-fleet-user-metrics', tenantId, userId] : null,
    () => getOpsBotMetrics(tenantId!, userId!),
  );

  if (!isSystemAdmin) {
    return (
      <BotMonitorShell title="Bot fleet" subtitle="We restrict this view to platform operators.">
        <p className="opacity-80">Sign in with a system administrator account to review customer bot fleets.</p>
      </BotMonitorShell>
    );
  }

  return (
    <BotMonitorShell
      title="Bot fleet"
      subtitle="Cross customer fleet view with drill down by organization and user. Server routes enforce operator access."
    >
      <nav className="mb-4 flex flex-wrap gap-2 text-sm">
        <button
          type="button"
          className="rounded px-3 py-1 border"
          style={{ borderColor: '#46224d', color: '#e3dbd3', backgroundColor: drill.level === 'fleet' ? '#46224d' : '#232427' }}
          onClick={() => setDrill({ level: 'fleet' })}
        >
          Fleet
        </button>
        {drill.level !== 'fleet' && (
          <button
            type="button"
            className="rounded px-3 py-1 border"
            style={{ borderColor: '#46224d', color: '#e3dbd3', backgroundColor: drill.level === 'customer' ? '#46224d' : '#232427' }}
            onClick={() => setDrill({ level: 'customer', tenant: drill.tenant })}
          >
            {drill.tenant.tenantName}
          </button>
        )}
        {drill.level === 'user' && (
          <span className="rounded px-3 py-1 border" style={{ borderColor: '#46224d', color: '#e3dbd3', backgroundColor: '#131315' }}>
            {drill.user.displayName}
          </span>
        )}
      </nav>

      {drill.level === 'fleet' && (
        <>
          {fleet.isLoading && <p className="opacity-70">Loading fleet...</p>}
          {fleet.error && <p className="opacity-80">Could not load fleet summary.</p>}
          {(fleet.data?.fleetTypeCounts?.length ?? 0) > 0 && (
            <div className="mb-4">
              <p className="text-xs opacity-70 mb-1">Fleet by bot type</p>
              <BotTypeCountChips counts={fleet.data!.fleetTypeCounts} />
            </div>
          )}
          <div className="grid gap-3 md:grid-cols-2 lg:grid-cols-3">
            {(fleet.data?.tenants ?? []).map((t) => (
              <button
                key={t.tenantId}
                type="button"
                className="rounded-lg border p-4 text-left"
                style={{ backgroundColor: '#232427', borderColor: '#46224d', color: '#e3dbd3' }}
                onClick={() => {
                  setTypeFilter('');
                  setDrill({ level: 'customer', tenant: t });
                }}
              >
                <p className="font-medium">{t.tenantName}</p>
                <p className="text-xs opacity-70 mt-2">
                  {t.botCount} bots, {t.onlineCount} online
                </p>
                {t.typeCounts && t.typeCounts.length > 0 && <BotTypeCountChips counts={t.typeCounts} />}
              </button>
            ))}
          </div>
        </>
      )}

      {drill.level === 'customer' && (
        <>
          {users.isLoading && <p className="opacity-70">Loading users...</p>}
          {!users.isLoading && (
            <div className="text-sm opacity-80 mb-4">
              <p>
                Organization total: {drill.tenant.botCount} bots
                {(users.data?.users ?? []).length > 0 && (
                  <>
                    {' '}
                    (assigned across users:{' '}
                    {(users.data?.users ?? []).reduce((sum, u) => sum + u.botCount, 0)})
                  </>
                )}
              </p>
              {(users.data?.typeCounts?.length ?? 0) > 0 && (
                <BotTypeCountChips counts={users.data!.typeCounts} />
              )}
            </div>
          )}
          <div className="grid gap-3 md:grid-cols-2">
            {(users.data?.users ?? []).map((u) => (
              <button
                key={u.userId}
                type="button"
                className="rounded-lg border p-4 text-left"
                style={{ backgroundColor: '#232427', borderColor: '#46224d', color: '#e3dbd3' }}
                onClick={() => {
                  setTypeFilter('');
                  setDrill({ level: 'user', tenant: drill.tenant, user: u });
                }}
              >
                <p className="font-medium">{u.displayName}</p>
                <p className="text-xs opacity-70">{u.email}</p>
                <p className="text-xs opacity-70 mt-2">{u.botCount} bots</p>
                {u.typeCounts && u.typeCounts.length > 0 && <BotTypeCountChips counts={u.typeCounts} />}
              </button>
            ))}
          </div>
        </>
      )}

      {drill.level === 'user' && (
        <>
          {userBots.isLoading && <p className="opacity-70">Loading bots...</p>}
          <div className="mb-4 flex flex-wrap items-center gap-2">
            <label className="text-xs opacity-80" htmlFor="ops-bot-type-filter">
              Bot type
            </label>
            <select
              id="ops-bot-type-filter"
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
            {(userBots.data?.typeCounts?.length ?? 0) > 0 && (
              <BotTypeCountChips counts={userBots.data!.typeCounts} />
            )}
          </div>
          <div className="overflow-x-auto">
            <table className={botMonitorTableClass()}>
              <thead>
                <tr>
                  {['Bot', 'Type', 'Platform', 'Presence', 'Last seen', 'Keep'].map((h) => (
                    <th key={h} className="px-3 py-2 text-left font-medium" style={botMonitorThStyle()}>
                      {h}
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {(userBots.data?.bots ?? []).map((bot) => (
                  <tr key={bot.botId}>
                    <td className="px-3 py-2" style={botMonitorTdStyle()}>
                      {bot.displayName}
                    </td>
                    <td className="px-3 py-2" style={botMonitorTdStyle()}>
                      {bot.botTypeKey ? (
                        <BotTypeBadge
                          typeKey={bot.botTypeKey}
                          displayName={bot.botTypeDisplayName ?? bot.botTypeKey}
                        />
                      ) : (
                        <span className="text-xs opacity-60">n/a</span>
                      )}
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
                    <td className="px-3 py-2" style={botMonitorTdStyle()}>
                      <KeepBadge value={bot.lastKeepCheck} />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {!userMetrics.isLoading && (userMetrics.data ?? []).length > 0 && (
            <div className="mt-8 space-y-8">
              {userMetrics.data!.map((row) => (
                <section
                  key={row.botId}
                  className="rounded-lg border p-4"
                  style={{ backgroundColor: '#232427', borderColor: '#46224d' }}
                >
                  <h2 className="text-lg font-medium mb-2">{row.displayName}</h2>
                  <BotMonitorMetricsTiles metrics={row.metrics} />
                  <BotMonitorMetricsCharts metrics={row.metrics} />
                </section>
              ))}
            </div>
          )}
        </>
      )}
    </BotMonitorShell>
  );
}
