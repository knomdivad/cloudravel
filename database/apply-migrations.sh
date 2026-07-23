#!/usr/bin/env bash
# ============================================================================
# Idempotent SQL migration runner for the local/OrbStack stack.
# Waits for SQL Server, creates the database, then applies each migration once
# (tracked in dbo.__migrations) so `docker compose up` is safe to re-run and
# picks up newly-added migrations without recreating the volume.
#
# Uses the classic ODBC-based sqlcmd (mssql-tools image), which tolerates SQL
# Server's self-signed cert; go-sqlcmd's stricter x509 parser rejects it.
#
# Expects: MSSQL_SA_PASSWORD env, migrations mounted at /database.
# ============================================================================
set -euo pipefail

SQLCMD=/opt/mssql-tools/bin/sqlcmd
SERVER=mssql
DB=aimdb
# Applied in order; the demo seed runs last (once) so a fresh stack has data.
MIGRATIONS=(
  001-schema
  002-fix-rls-bypass
  003-aiops-multicloud
  004-local-auth
  005-job-queue
  006-rename-secret-column
  seed-demo-data
)

run() { "$SQLCMD" -S "$SERVER" -U sa -P "$MSSQL_SA_PASSWORD" "$@"; }

echo "Waiting for SQL Server at $SERVER..."
for i in $(seq 1 60); do
  if run -Q "SELECT 1" -b >/dev/null 2>&1; then
    echo "SQL Server is up."
    break
  fi
  if [ "$i" -eq 60 ]; then
    echo "ERROR: SQL Server did not become ready in time." >&2
    exit 1
  fi
  sleep 2
done

echo "Ensuring database '$DB' exists..."
run -b -Q "IF DB_ID(N'$DB') IS NULL CREATE DATABASE [$DB];"

echo "Ensuring migration ledger exists..."
run -d "$DB" -b -Q "IF OBJECT_ID(N'dbo.__migrations') IS NULL CREATE TABLE dbo.__migrations (name NVARCHAR(255) NOT NULL PRIMARY KEY, applied_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());"

for m in "${MIGRATIONS[@]}"; do
  applied=$(run -d "$DB" -h -1 -W -b -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM dbo.__migrations WHERE name = N'$m';" | tr -d '[:space:]')
  if [ "$applied" = "0" ]; then
    echo "Applying: $m"
    run -d "$DB" -b -i "/database/$m.sql"
    run -d "$DB" -b -Q "INSERT INTO dbo.__migrations (name) VALUES (N'$m');"
    echo "  → $m applied."
  else
    echo "Skipping already-applied: $m"
  fi
done

echo "All migrations complete."
