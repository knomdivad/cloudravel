#!/usr/bin/env bash
# Verifies 006-bot-monitor-org-fk.sql deletes orphan child rows then adds the FK.
# Idempotent: runs 006 twice. Expects sqlcmd and a fresh or BotMonitor-ready DB.
set -euo pipefail

SQLCMD=${SQLCMD:-/opt/mssql-tools/bin/sqlcmd}
SERVER=${DB_HOST:-localhost}
DB_USER=${DB_USER:-sa}
DB=${DB_NAME:-cloudraveldb}
PASSWORD=${MSSQL_SA_PASSWORD:?Set MSSQL_SA_PASSWORD}

run() { "$SQLCMD" -S "$SERVER" -U "$DB_USER" -P "$PASSWORD" -I "$@"; }

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "Waiting for SQL Server at ${SERVER}..."
for i in $(seq 1 60); do
  if run -Q "SELECT 1" -b >/dev/null 2>&1; then
    break
  fi
  if [ "$i" -eq 60 ]; then
    echo "ERROR: SQL Server not ready." >&2
    exit 1
  fi
  sleep 2
done

run -b -Q "IF DB_ID(N'${DB}') IS NULL CREATE DATABASE [${DB}];"

has_bot_monitor=$(run -d "$DB" -h -1 -W -b -Q "SET NOCOUNT ON; SELECT CASE WHEN OBJECT_ID(N'dbo.bot_monitor_tenants') IS NULL THEN 0 ELSE 1 END;" | tr -d '[:space:]')
if [ "$has_bot_monitor" != "1" ]; then
  echo "Applying minimal BotMonitor schema for 006 test..."
  cat "${SCRIPT_DIR}/test-006-bot-monitor-min-schema.sql" | run -d "$DB" -b
fi

ORPHAN='A0000000-0000-0000-0000-000000000099'
BOT='A0000000-0000-0000-0000-000000000098'

run -d "$DB" -b -Q "
SET NOCOUNT ON;
IF NOT EXISTS (SELECT 1 FROM bot_monitor_tenants WHERE tenant_id = '${ORPHAN}')
    INSERT INTO bot_monitor_tenants (tenant_id, name) VALUES ('${ORPHAN}', N'orphan-test');
IF NOT EXISTS (SELECT 1 FROM bot_monitor_bots WHERE bot_id = '${BOT}')
    INSERT INTO bot_monitor_bots (bot_id, tenant_id, display_name, platform, token_hash, hmac_secret_enc)
    VALUES ('${BOT}', '${ORPHAN}', N'orphan-bot', N'other', REPLICATE('a', 64), N'test.enc');
IF NOT EXISTS (SELECT 1 FROM bot_monitor_heartbeats WHERE bot_id = '${BOT}')
    INSERT INTO bot_monitor_heartbeats (bot_id, tenant_id, status, keep_check)
    VALUES ('${BOT}', '${ORPHAN}', N'ok', N'none');
IF NOT EXISTS (SELECT 1 FROM bot_monitor_bot_state WHERE bot_id = '${BOT}')
    INSERT INTO bot_monitor_bot_state (bot_id, tenant_id) VALUES ('${BOT}', '${ORPHAN}');
IF NOT EXISTS (SELECT 1 FROM bot_monitor_heartbeat_rate WHERE bot_id = '${BOT}')
    INSERT INTO bot_monitor_heartbeat_rate (bot_id, window_start, request_count)
    VALUES ('${BOT}', SYSUTCDATETIME(), 1);
IF NOT EXISTS (SELECT 1 FROM bot_monitor_nonces WHERE bot_id = '${BOT}' AND ts = 1)
    INSERT INTO bot_monitor_nonces (bot_id, ts, sig) VALUES ('${BOT}', 1, REPLICATE('b', 64));
IF NOT EXISTS (SELECT 1 FROM bot_monitor_magic_links WHERE link_id = '${BOT}')
    INSERT INTO bot_monitor_magic_links (link_id, tenant_id, token_hash, expires_at)
    VALUES ('${BOT}', '${ORPHAN}', REPLICATE('c', 64), DATEADD(day, 1, SYSUTCDATETIME()));
"

for pass in 1 2; do
  echo "Running 006-bot-monitor-org-fk.sql (pass ${pass})..."
  cat "${SCRIPT_DIR}/006-bot-monitor-org-fk.sql" | run -d "$DB" -b
done

orphan_count=$(run -d "$DB" -h -1 -W -b -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM bot_monitor_tenants WHERE tenant_id = '${ORPHAN}';" | tr -d '[:space:]')
fk_count=$(run -d "$DB" -h -1 -W -b -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.foreign_keys WHERE name = N'FK_bot_monitor_tenants_organization';" | tr -d '[:space:]')

if [ "$orphan_count" != "0" ]; then
  echo "ERROR: orphan tenant still present (count=${orphan_count})." >&2
  exit 1
fi
if [ "$fk_count" != "1" ]; then
  echo "ERROR: expected FK_bot_monitor_tenants_organization (count=${fk_count})." >&2
  exit 1
fi

echo "006 orphan cleanup check passed."
