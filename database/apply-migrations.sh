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
# Host of the SQL Server to migrate. Defaults to the compose service name;
# Kubernetes sets DB_HOST to the mssql Service name (or an external server).
SERVER=${DB_HOST:-mssql}
# Admin login. Defaults to sa (bundled SQL Edge); override DB_USER for an
# external managed server whose admin isn't named sa.
DB_USER=${DB_USER:-sa}
DB=cloudraveldb
LEGACY_DB=aimdb
# Schema/RBAC migrations only. The bootstrap local admin is created by
# 004-local-auth (+ 011 ensures system_admin). Demo data is NOT applied —
# load seed-demo-data.sql manually if you want Contoso sample rows.
MIGRATIONS=(
  001-schema
  002-fix-rls-bypass
  003-aiops-multicloud
  004-local-auth
  005-job-queue
  006-rename-secret-column
  007-cloud-orgs
  008-organizations
  009-azure-multi-tenant
  010-admin-rbac
  011-ensure-bootstrap-admin
  012-rls-hardening
)

# -I: QUOTED_IDENTIFIER ON (required for filtered indexes / tables that have them;
#     sqlcmd defaults OFF, which breaks UPDATEs on users after 004-local-auth).
run() { "$SQLCMD" -S "$SERVER" -U "$DB_USER" -P "$MSSQL_SA_PASSWORD" -I "$@"; }

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
