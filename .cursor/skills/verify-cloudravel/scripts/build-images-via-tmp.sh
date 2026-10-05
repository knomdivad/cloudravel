#!/usr/bin/env bash
# Work around Docker BuildKit COPY failures when the build context lives on the
# Cloud Agent workspace mount. Extracts src/backend and src/frontend to /tmp
# and builds cloudravel-api:local and cloudravel-web:local there.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../../.." && pwd)"
DOCKER=( docker )
if ! docker info >/dev/null 2>&1; then DOCKER=( sudo docker ); fi
GIT_SHA="${GIT_SHA:-$(git -C "${REPO_ROOT}" rev-parse --short HEAD 2>/dev/null || true)}"

tmpdir="$(mktemp -d)"
trap 'rm -rf "${tmpdir}"' EXIT

mkdir -p "${tmpdir}/backend" "${tmpdir}/frontend"
tar -C "${REPO_ROOT}/src/backend" -cf - . | tar -C "${tmpdir}/backend" -xf -
tar -C "${REPO_ROOT}/src/frontend" -cf - . | tar -C "${tmpdir}/frontend" -xf -

echo "Building cloudravel-api:local from ${tmpdir}/backend ..."
"${DOCKER[@]}" build -t cloudravel-api:local \
  --build-arg "GIT_SHA=${GIT_SHA}" \
  -f "${tmpdir}/backend/Dockerfile" "${tmpdir}/backend"

echo "Building cloudravel-web:local from ${tmpdir}/frontend ..."
"${DOCKER[@]}" build -t cloudravel-web:local \
  --build-arg "NEXT_PUBLIC_API_BASE_URL=/api" \
  --build-arg "NEXT_PUBLIC_AZURE_AD_CLIENT_ID=" \
  --build-arg "NEXT_PUBLIC_AZURE_AD_TENANT_ID=" \
  --build-arg "NEXT_PUBLIC_API_SCOPE=" \
  -f "${tmpdir}/frontend/Dockerfile" "${tmpdir}/frontend"

echo "OK: cloudravel-api:local and cloudravel-web:local"
