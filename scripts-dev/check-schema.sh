#!/usr/bin/env bash
# Verify the assessment schema columns exist in the live database.
set -euo pipefail
cd "$(cd "$(dirname "$0")/.." && pwd)"
# shellcheck disable=SC1091
. ./.env
docker run --rm --network t_de701540_default mcr.microsoft.com/mssql-tools:latest \
  /opt/mssql-tools/bin/sqlcmd -S mssql -U sa -P "$MSSQL_SA_PASSWORD" -I -d cloudraveldb \
  -Q "SELECT name FROM sys.columns WHERE object_id = OBJECT_ID('dbo.tenants') AND name LIKE 'assessment%' OR object_id = OBJECT_ID('dbo.tenants') AND name = 'engagement_kind'; SELECT TOP 2 name FROM dbo.__migrations ORDER BY name DESC;"
