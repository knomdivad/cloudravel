#!/usr/bin/env bash
# BotMonitor repository + ops/bots integration tests against disposable SQL Server.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
CONTAINER="mssql-botmon-$$"
MSSQL_IMAGE="${MSSQL_IMAGE:-mcr.microsoft.com/mssql/server:2022-latest}"
TOOLS_IMAGE="mcr.microsoft.com/mssql-tools:latest"
SA_PASSWORD="${MSSQL_SA_PASSWORD:-CloudRavelTest123!}"
DB=cloudraveldb
HOST_PORT=14334

cleanup() {
  docker rm -f "${CONTAINER}" >/dev/null 2>&1 || true
}
trap cleanup EXIT

echo "Starting SQL Server (${MSSQL_IMAGE}) on port ${HOST_PORT}..."
docker run -d --name "${CONTAINER}" \
  -p "${HOST_PORT}:1433" \
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

echo "Applying migrations..."
docker run --rm --network "container:${CONTAINER}" \
  -e MSSQL_SA_PASSWORD="${SA_PASSWORD}" \
  -e DB_HOST=localhost \
  -v "${SCRIPT_DIR}:/database:ro" \
  "${TOOLS_IMAGE}" \
  bash -c "sed 's/^DB=cloudraveldb\$/DB=${DB}/' /database/apply-migrations.sh | tr -d '\\015' | bash"

echo "Seeding BotMonitor integration rows..."
run_sql -d "${DB}" -b -i /database/test-bot-monitor-seed.sql

export CLOUDRAVEL_INTEGRATION_SQL_CONNECTION="Server=127.0.0.1,${HOST_PORT};Database=${DB};User Id=sa;Password=${SA_PASSWORD};TrustServerCertificate=True"

echo "Running BotMonitor SQL integration tests..."
cd "${REPO_ROOT}"
dotnet restore CloudRavel.sln --locked-mode
dotnet test CloudRavel.sln --configuration Release --no-restore --filter "Category=BotMonitorSqlIntegration" --verbosity minimal

echo "BotMonitor SQL integration tests passed."
