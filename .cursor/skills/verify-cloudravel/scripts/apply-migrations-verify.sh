#!/usr/bin/env bash
# Verification copy of database/apply-migrations.sh — omits 002-assessment (no .sql
# in tree; 003-drop-assessment is a no-op when assessment objects never existed).
# Mounted over /database/apply-migrations.sh by compose.verify.yml only during verify runs.
set -euo pipefail

SQLCMD=/opt/mssql-tools/bin/sqlcmd
SERVER=${DB_HOST:-mssql}
DB_USER=${DB_USER:-sa}
DB=cloudraveldb
LEGACY_DB=aimdb
MIGRATIONS=(
  001-schema
  003-drop-assessment
  004-assessment-principal
)

run() { "$SQLCMD" -S "$SERVER" -U "$DB_USER" -P "$MSSQL_SA_PASSWORD" -I "$@"; }

echo "Waiting for SQL Server at $SERVER..."
for i in $(seq 1 90); do
  if run -Q "SELECT 1" -b >/dev/null 2>&1; then
    echo "SQL Server is up."
    break
  fi
  if [ "$i" -eq 90 ]; then
    echo "ERROR: SQL Server did not become ready in time." >&2
    exit 1
  fi
  sleep 2
done

echo "Ensuring database '$DB' exists..."
if run -b -Q "SET NOCOUNT ON; SELECT 1;" >/dev/null 2>&1 && \
   [ "$(run -h -1 -W -b -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.databases WHERE name = N'$DB';" | tr -d '[:space:]')" = "0" ] && \
   [ "$(run -h -1 -W -b -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.databases WHERE name = N'$LEGACY_DB';" | tr -d '[:space:]')" = "1" ]; then
  echo "Renaming existing database '$LEGACY_DB' -> '$DB' (data-preserving, one-time)..."
  run -b -Q "ALTER DATABASE [$LEGACY_DB] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;"
  run -b -Q "ALTER DATABASE [$LEGACY_DB] MODIFY NAME = [$DB];"
  run -b -Q "ALTER DATABASE [$DB] SET MULTI_USER;"
fi
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
