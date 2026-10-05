#!/usr/bin/env bash
# End-to-end proof for feature local-login: deps → API/web → browser → cleanup.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SKILL_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
REPO_ROOT="$(cd "${SKILL_ROOT}/../../.." && pwd)"
export PATH="${SCRIPT_DIR}:${PATH}"

: "${VERIFY_RUN_ID:=$(openssl rand -hex 4)}"
: "${VERIFY_ARTIFACTS_ROOT:=/opt/cursor/artifacts/verify-cloudravel}"
STATE_DIR="${VERIFY_ARTIFACTS_ROOT}/state"
mkdir -p "${STATE_DIR}" "${VERIFY_ARTIFACTS_ROOT}/${VERIFY_RUN_ID}"

DOCKER=( docker )
if ! docker info >/dev/null 2>&1; then DOCKER=( sudo docker ); fi

COMPOSE_FILES=( -f "${REPO_ROOT}/docker-compose.yml" -f "${SKILL_ROOT}/compose.deps.yml" )
PROJECT="cloudravel-verify-${VERIFY_RUN_ID}"

if [[ ! -f "${REPO_ROOT}/.env" ]]; then
  cp "${REPO_ROOT}/.env.example" "${REPO_ROOT}/.env"
fi

# Dev-mode OpenBao avoids file-storage init failures on fresh volumes in CI-like VMs.
export COMPOSE_PROJECT_NAME="${PROJECT}"
export OPENBAO_MODE=dev
export OPENBAO_TOKEN=root
export GIT_SHA="${GIT_SHA:-$(git -C "${REPO_ROOT}" rev-parse --short HEAD 2>/dev/null || true)}"

WEB_URL="http://127.0.0.1:3000"
API_URL="http://127.0.0.1:7071/api"

cat > "${STATE_DIR}/run.env" <<ENV
VERIFY_RUN_ID=${VERIFY_RUN_ID}
COMPOSE_PROJECT_NAME=${PROJECT}
WEB_HOST_PORT=3000
API_HOST_PORT=7071
WEB_URL=${WEB_URL}
API_URL=${API_URL}
REPO_ROOT=${REPO_ROOT}
VERIFY_ARTIFACTS_ROOT=${VERIFY_ARTIFACTS_ROOT}
ENV

echo "=== prove-local-login run_id=${VERIFY_RUN_ID} project=${PROJECT} ==="

cleanup() {
  echo "=== cleanup ==="
  if [[ -f "${STATE_DIR}/api.pid" ]]; then
    kill "$(cat "${STATE_DIR}/api.pid")" 2>/dev/null || true
    rm -f "${STATE_DIR}/api.pid"
  fi
  if [[ -f "${STATE_DIR}/web.pid" ]]; then
    kill "$(cat "${STATE_DIR}/web.pid")" 2>/dev/null || true
    rm -f "${STATE_DIR}/web.pid"
  fi
  (cd "${REPO_ROOT}" && COMPOSE_PROJECT_NAME="${PROJECT}" OPENBAO_MODE=dev OPENBAO_TOKEN=root \
    "${DOCKER[@]}" compose "${COMPOSE_FILES[@]}" down) || true
}
trap cleanup EXIT

echo "=== launch deps (mssql, azurite, openbao dev) ==="
(cd "${REPO_ROOT}" && COMPOSE_PROJECT_NAME="${PROJECT}" OPENBAO_MODE=dev OPENBAO_TOKEN=root \
  "${DOCKER[@]}" compose "${COMPOSE_FILES[@]}" up -d mssql azurite openbao)

echo "=== migrator ==="
(cd "${REPO_ROOT}" && COMPOSE_PROJECT_NAME="${PROJECT}" OPENBAO_MODE=dev OPENBAO_TOKEN=root \
  "${DOCKER[@]}" compose "${COMPOSE_FILES[@]}" run --rm migrator)

echo "=== host API + web (docker build may fail on workspace mount) ==="
export DOTNET_ROOT="${HOME}/.dotnet"
export PATH="${DOTNET_ROOT}:${HOME}/.nvm/versions/node/v20.20.2/bin:${HOME}/.local/bin:${PATH}"
[[ -s "${HOME}/.nvm/nvm.sh" ]] && . "${HOME}/.nvm/nvm.sh" && nvm use 20 >/dev/null 2>&1 || true

STORAGE='DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;QueueEndpoint=http://127.0.0.1:10001/devstoreaccount1;TableEndpoint=http://127.0.0.1:10002/devstoreaccount1'
SA_PASS="$(grep '^MSSQL_SA_PASSWORD=' "${REPO_ROOT}/.env" | cut -d= -f2-)"

(
  cd "${REPO_ROOT}/src/backend/CloudRavel.Api"
  export AzureWebJobsStorage="${STORAGE}"
  export FUNCTIONS_WORKER_RUNTIME=dotnet-isolated
  export Platform__Environment=Development
  export SqlConnectionString="Server=127.0.0.1,1433;Database=cloudraveldb;User ID=sa;Password=${SA_PASS};Encrypt=True;TrustServerCertificate=True;"
  export OpenBao__Address="http://127.0.0.1:8200"
  export OpenBao__Token=root
  export LocalAuth__JwtSigningKey="${LOCAL_AUTH_JWT_SIGNING_KEY:-dev-only-change-me-in-production}"
  export Cors__AllowedOrigins="${WEB_URL}"
  export SecretStore__Provider=OpenBao
  export AzureWebJobs.PollChangesTimer.Disabled=true
  export AzureWebJobs.SyncAdvisorTimer.Disabled=true
  export AzureWebJobs.SyncPolicyTimer.Disabled=true
  export AzureWebJobs.SyncDefenderTimer.Disabled=true
  dotnet run --no-launch-profile > "${VERIFY_ARTIFACTS_ROOT}/${VERIFY_RUN_ID}/api.log" 2>&1 &
  echo $! > "${STATE_DIR}/api.pid"
)

(
  cd "${REPO_ROOT}/src/frontend"
  export NEXT_PUBLIC_API_BASE_URL="${API_URL}"
  npm run dev -- --port 3000 > "${VERIFY_ARTIFACTS_ROOT}/${VERIFY_RUN_ID}/web.log" 2>&1 &
  echo $! > "${STATE_DIR}/web.pid"
)

sleep 15
control-cloudravel doctor

echo "=== browser-login proof ==="
control-cloudravel browser-login

echo "=== post-cleanup evidence check ==="
cleanup
trap - EXIT

test -f "${VERIFY_ARTIFACTS_ROOT}/${VERIFY_RUN_ID}/local-login/post-login.png"
echo "PASS: evidence at ${VERIFY_ARTIFACTS_ROOT}/${VERIFY_RUN_ID}/local-login/"
