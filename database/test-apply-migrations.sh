#!/usr/bin/env bash
# End-to-end migrator test against a disposable SQL Server container.
# Uses --network container:<mssql> so sqlcmd reaches localhost:1433 (works on
# CI runners where bridge DNS/TCP between separate containers is unreliable).
#
# 1) Fresh database: full apply-migrations.sh succeeds.
# 2) 001-only ledger: simulates a volume that applied 001 then failed on missing 002-assessment.
#
# Requires: docker, linux/amd64 mssql/server + mssql-tools images.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CONTAINER="mssql-migtest-$$"
MSSQL_IMAGE="${MSSQL_IMAGE:-mcr.microsoft.com/mssql/server:2022-latest}"
TOOLS_IMAGE="mcr.microsoft.com/mssql-tools:latest"
SA_PASSWORD="${MSSQL_SA_PASSWORD:-CloudRavelTest123!}"
DB=cloudraveldb

cleanup() {
  docker rm -f "${CONTAINER}" >/dev/null 2>&1 || true
}
trap cleanup EXIT

echo "Starting SQL Server (${MSSQL_IMAGE})..."
docker run -d --name "${CONTAINER}" \
  -e ACCEPT_EULA=Y \
  -e MSSQL_SA_PASSWORD="${SA_PASSWORD}" \
  "${MSSQL_IMAGE}" >/dev/null

run_sql() {
  docker run --rm --network "container:${CONTAINER}" \
    -e MSSQL_SA_PASSWORD="${SA_PASSWORD}" \
    -v "${SCRIPT_DIR}:/database:ro" \
    "${TOOLS_IMAGE}" \
    /opt/mssql-tools/bin/sqlcmd -S localhost -U sa -P "${SA_PASSWORD}" -I "$@"
}

echo "Waiting for SQL Server..."
for i in $(seq 1 90); do
  if run_sql -Q "SELECT 1" -b >/dev/null 2>&1; then
    echo "SQL Server is up."
    break
  fi
  if [[ "${i}" -eq 90 ]]; then
    echo "ERROR: SQL Server did not become ready." >&2
    exit 1
  fi
  sleep 2
done

run_migrator() {
  local db_name=${1:-${DB}}
  docker run --rm --network "container:${CONTAINER}" \
    -e MSSQL_SA_PASSWORD="${SA_PASSWORD}" \
    -e DB_HOST=localhost \
    -v "${SCRIPT_DIR}:/database:ro" \
    "${TOOLS_IMAGE}" \
    bash -c "sed 's/^DB=cloudraveldb\$/DB=${db_name}/' /database/apply-migrations.sh | tr -d '\\015' | bash"
}

echo "=== Scenario A: fresh database ==="
run_migrator "${DB}"

count_a=$(run_sql -d "${DB}" -h -1 -W -b -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM dbo.__migrations;" | tr -d '[:space:]')
if [[ "${count_a}" -lt 10 ]]; then
  echo "ERROR: expected many ledger rows on fresh DB, got ${count_a}" >&2
  exit 1
fi
if [[ "$(run_sql -d "${DB}" -h -1 -W -b -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM dbo.__migrations WHERE name = N'002-assessment';" | tr -d '[:space:]')" != "0" ]]; then
  echo "ERROR: 002-assessment should not be in ledger after fix" >&2
  exit 1
fi
chat_a=$(run_sql -d "${DB}" -h -1 -W -b -Q "SET NOCOUNT ON; SELECT CASE WHEN OBJECT_ID(N'dbo.chat_usage_counters', N'U') IS NULL THEN 0 ELSE 1 END;" | tr -d '[:space:]')
if [[ "${chat_a}" != "1" ]]; then
  echo "ERROR: expected chat_usage_counters on fresh DB" >&2
  exit 1
fi
echo "Fresh DB: ${count_a} migrations applied, chat_usage_counters present."

echo "=== Scenario B: 001-only ledger (stuck dev volume) ==="
DB_B=cloudraveldb_001only
run_sql -b -Q "IF DB_ID(N'${DB_B}') IS NOT NULL DROP DATABASE [${DB_B}]; CREATE DATABASE [${DB_B}];"
run_sql -d "${DB_B}" -b -i "/database/001-schema.sql"
run_sql -d "${DB_B}" -b -Q "
IF OBJECT_ID(N'dbo.__migrations') IS NULL
  CREATE TABLE dbo.__migrations (name NVARCHAR(255) NOT NULL PRIMARY KEY, applied_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
IF NOT EXISTS (SELECT 1 FROM dbo.__migrations WHERE name = N'001-schema')
  INSERT INTO dbo.__migrations (name) VALUES (N'001-schema');
"

run_migrator "${DB_B}"

count_b=$(run_sql -d "${DB_B}" -h -1 -W -b -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM dbo.__migrations;" | tr -d '[:space:]')
bot=$(run_sql -d "${DB_B}" -h -1 -W -b -Q "SET NOCOUNT ON; SELECT CASE WHEN OBJECT_ID(N'dbo.bot_monitor_tenants') IS NULL THEN 0 ELSE 1 END;" | tr -d '[:space:]')
if [[ "${count_b}" -lt "${count_a}" ]]; then
  echo "ERROR: 001-only DB should reach same migration count (got ${count_b}, fresh ${count_a})" >&2
  exit 1
fi
if [[ "${bot}" != "1" ]]; then
  echo "ERROR: expected bot_monitor schema after catching up from 001-only ledger" >&2
  exit 1
fi
chat_b=$(run_sql -d "${DB_B}" -h -1 -W -b -Q "SET NOCOUNT ON; SELECT CASE WHEN OBJECT_ID(N'dbo.chat_usage_counters', N'U') IS NULL THEN 0 ELSE 1 END;" | tr -d '[:space:]')
if [[ "${chat_b}" != "1" ]]; then
  echo "ERROR: expected chat_usage_counters after 001-only catch-up" >&2
  exit 1
fi
echo "001-only ledger catch-up: ${count_b} migrations, bot_monitor and chat_usage_counters present."

echo "apply-migrations integration test passed."
