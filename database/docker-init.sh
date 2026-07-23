#!/usr/bin/env bash
# Applies database/*.sql migrations in order against the `sql` container on
# first boot. Skips entirely if the `aimdb` database already exists — safe to
# run again on every `docker compose up` as long as the SQL volume persists.
#
# Installs mssql-tools18 from Microsoft's apt repo (arm64-capable) rather than
# using go-sqlcmd: go-sqlcmd's stricter Go x509 parser rejects SQL Server's
# self-signed cert (negative serial number — a known SQL Server quirk), while
# the ODBC-based classic sqlcmd tolerates it fine.
set -euo pipefail

if ! command -v sqlcmd >/dev/null 2>&1; then
    echo "Installing mssql-tools18..."
    apt-get update -qq && apt-get install -y -qq gnupg curl > /dev/null
    curl -sL https://packages.microsoft.com/keys/microsoft.asc | gpg --batch --yes --dearmor -o /usr/share/keyrings/microsoft-prod.gpg
    curl -sL https://packages.microsoft.com/config/debian/12/prod.list -o /etc/apt/sources.list.d/mssql-release.list
    apt-get update -qq
    ACCEPT_EULA=Y apt-get install -y -qq mssql-tools18 unixodbc-dev > /dev/null
    export PATH="$PATH:/opt/mssql-tools18/bin"
fi

HOST="sql"

echo "Waiting for $HOST:1433..."
for i in $(seq 1 60); do
    if sqlcmd -S "$HOST" -U sa -P "$SQL_SA_PASSWORD" -C -Q "SELECT 1" >/dev/null 2>&1; then
        echo "SQL is up."
        break
    fi
    if [ "$i" = "60" ]; then
        echo "Timed out waiting for SQL."
        exit 1
    fi
    sleep 2
done

DB_EXISTS=$(sqlcmd -S "$HOST" -U sa -P "$SQL_SA_PASSWORD" -h -1 -C \
    -Q "SET NOCOUNT ON; SELECT CASE WHEN DB_ID('aimdb') IS NULL THEN 0 ELSE 1 END" | tr -d '[:space:]')

if [ "$DB_EXISTS" = "1" ]; then
    echo "aimdb already exists — skipping migrations."
    exit 0
fi

echo "Creating aimdb..."
sqlcmd -S "$HOST" -U sa -P "$SQL_SA_PASSWORD" -C -Q "CREATE DATABASE aimdb"

for f in /database/001-schema.sql \
         /database/002-fix-rls-bypass.sql \
         /database/003-aiops-multicloud.sql \
         /database/004-local-auth.sql \
         /database/005-job-queue.sql \
         /database/006-rename-secret-column.sql; do
    echo "Applying $f..."
    sqlcmd -S "$HOST" -U sa -P "$SQL_SA_PASSWORD" -C -d aimdb -i "$f"
done

echo "All migrations applied."
