'use client';

import React, { useState } from 'react';
import useSWR from 'swr';
import {
  getOpsFleet,
  getOpsFleetUserBots,
  getOpsFleetUsers,
} from '../../../lib/api';
import type { BotMonitorFleetTenant, BotMonitorFleetUser } from '../../../lib/types';
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

  const fleet = useSWR(isSystemAdmin ? 'ops-fleet' : null, getOpsFleet);
  const tenantId = drill.level !== 'fleet' ? drill.tenant.tenantId : null;
  const users = useSWR(tenantId && drill.level !== 'fleet' ? ['ops-fleet-users', tenantId] : null, () =>
    getOpsFleetUsers(tenantId!),
  );
  const userId = drill.level === 'user' ? drill.user.userId : null;
  const userBots = useSWR(
    tenantId && userId ? ['ops-fleet-user-bots', tenantId, userId] : null,
    () => getOpsFleetUserBots(tenantId!, userId!),
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
          <div className="grid gap-3 md:grid-cols-2 lg:grid-cols-3">
            {(fleet.data ?? []).map((t) => (
              <button
                key={t.tenantId}
                type="button"
                className="rounded-lg border p-4 text-left"
                style={{ backgroundColor: '#232427', borderColor: '#46224d', color: '#e3dbd3' }}
                onClick={() => setDrill({ level: 'customer', tenant: t })}
              >
                <p className="font-medium">{t.tenantName}</p>
                <p className="text-xs opacity-70 mt-2">
                  {t.botCount} bots, {t.onlineCount} online
                </p>
              </button>
            ))}
          </div>
        </>
      )}

      {drill.level === 'customer' && (
        <>
          {users.isLoading && <p className="opacity-70">Loading users...</p>}
          <div className="grid gap-3 md:grid-cols-2">
            {(users.data ?? []).map((u) => (
              <button
                key={u.userId}
                type="button"
                className="rounded-lg border p-4 text-left"
                style={{ backgroundColor: '#232427', borderColor: '#46224d', color: '#e3dbd3' }}
                onClick={() => setDrill({ level: 'user', tenant: drill.tenant, user: u })}
              >
                <p className="font-medium">{u.displayName}</p>
                <p className="text-xs opacity-70">{u.email}</p>
                <p className="text-xs opacity-70 mt-2">{u.botCount} bots</p>
              </button>
            ))}
          </div>
        </>
      )}

      {drill.level === 'user' && (
        <>
          {userBots.isLoading && <p className="opacity-70">Loading bots...</p>}
          <div className="overflow-x-auto">
            <table className={botMonitorTableClass()}>
              <thead>
                <tr>
                  {['Bot', 'Platform', 'Presence', 'Last seen', 'Keep'].map((h) => (
                    <th key={h} className="px-3 py-2 text-left font-medium" style={botMonitorThStyle()}>
                      {h}
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {(userBots.data ?? []).map((bot) => (
                  <tr key={bot.botId}>
                    <td className="px-3 py-2" style={botMonitorTdStyle()}>
                      {bot.displayName}
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
        </>
      )}
    </BotMonitorShell>
  );
}
