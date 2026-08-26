#!/usr/bin/env bash
#
# Cloud Agent bootstrap for CloudRavel.
#
# Installs the toolchain the repository actually builds with, then warms the
# dependency caches. Every step is idempotent: this runs on a fresh VM and again
# against cached state, so each install is skipped when the pinned version is
# already present.
#
# Versions are pinned to match CI (.github/workflows/build.yml) and global.json
# so an agent cannot pass locally on a toolchain the pipeline never uses.

set -euo pipefail

# Keep in sync with global.json (sdk.version) and build.yml (DOTNET_VERSION).
readonly DOTNET_CHANNEL="8.0"
# Keep in sync with build.yml (NODE_VERSION) and src/frontend/Dockerfile.
readonly NODE_VERSION="20"
# Keep in sync with build.yml (hashicorp/setup-terraform terraform_version '~> 1.9').
readonly TERRAFORM_VERSION="1.9.8"
# Keep in sync with build.yml (azure/setup-helm version).
readonly HELM_VERSION="v3.16.4"

readonly DOTNET_ROOT_DIR="${HOME}/.dotnet"
readonly LOCAL_BIN="${HOME}/.local/bin"
readonly REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

log() { printf '\n=== %s\n' "$1"; }

mkdir -p "${LOCAL_BIN}"
export PATH="${DOTNET_ROOT_DIR}:${LOCAL_BIN}:${PATH}"

# ---------------------------------------------------------------------------
# .NET 8 SDK
# ---------------------------------------------------------------------------
log ".NET SDK ${DOTNET_CHANNEL}"
if "${DOTNET_ROOT_DIR}/dotnet" --list-sdks 2>/dev/null | grep -q "^${DOTNET_CHANNEL}\."; then
  echo "Already installed: $("${DOTNET_ROOT_DIR}/dotnet" --version)"
else
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  chmod +x /tmp/dotnet-install.sh
  /tmp/dotnet-install.sh --channel "${DOTNET_CHANNEL}" --install-dir "${DOTNET_ROOT_DIR}"
  rm -f /tmp/dotnet-install.sh
fi

# ---------------------------------------------------------------------------
# Node — the default image ships a newer major than the one the app is built
# with, so pin to the CI/Dockerfile version rather than inheriting it.
# ---------------------------------------------------------------------------
log "Node ${NODE_VERSION}"
if [[ -s "${HOME}/.nvm/nvm.sh" ]]; then
  # nvm.sh is not written against `set -u`.
  set +u
  # shellcheck disable=SC1091
  source "${HOME}/.nvm/nvm.sh"
  nvm install "${NODE_VERSION}"
  nvm alias default "${NODE_VERSION}"
  nvm use default
  # `nvm use` only rewrites an nvm entry already on PATH, and this image puts
  # /exec-daemon (its own newer node) ahead of that entry. Without an explicit
  # prepend you get nvm's npm paired with the image's node — a silent major
  # version mismatch. Prepend so the pinned major actually wins.
  [[ -n "${NVM_BIN:-}" ]] && export PATH="${NVM_BIN}:${PATH}"
  set -u
else
  echo "nvm not present; using system node $(node --version 2>/dev/null || echo 'none')"
fi

# ---------------------------------------------------------------------------
# Terraform + Helm — needed to validate infra/ and deploy/helm/ locally the way
# the infra-validate and chart CI jobs do.
# ---------------------------------------------------------------------------
log "Terraform ${TERRAFORM_VERSION}"
if [[ "$("${LOCAL_BIN}/terraform" version -json 2>/dev/null | grep -o '"terraform_version":"[^"]*"' || true)" == *"${TERRAFORM_VERSION}"* ]]; then
  echo "Already installed: ${TERRAFORM_VERSION}"
else
  curl -fsSL -o /tmp/terraform.zip \
    "https://releases.hashicorp.com/terraform/${TERRAFORM_VERSION}/terraform_${TERRAFORM_VERSION}_linux_amd64.zip"
  unzip -oq /tmp/terraform.zip -d "${LOCAL_BIN}"
  rm -f /tmp/terraform.zip
fi

log "Helm ${HELM_VERSION}"
if [[ "$("${LOCAL_BIN}/helm" version --short 2>/dev/null || true)" == "${HELM_VERSION}"* ]]; then
  echo "Already installed: ${HELM_VERSION}"
else
  curl -fsSL -o /tmp/helm.tar.gz "https://get.helm.sh/helm-${HELM_VERSION}-linux-amd64.tar.gz"
  tar -xzf /tmp/helm.tar.gz -C /tmp
  install -m 0755 /tmp/linux-amd64/helm "${LOCAL_BIN}/helm"
  rm -rf /tmp/helm.tar.gz /tmp/linux-amd64
fi

# ---------------------------------------------------------------------------
# Make the toolchain visible to interactive shells the agent opens later.
# ---------------------------------------------------------------------------
readonly PROFILE_MARKER="# >>> cloudravel toolchain >>>"
if ! grep -qF "${PROFILE_MARKER}" "${HOME}/.bashrc" 2>/dev/null; then
  cat >> "${HOME}/.bashrc" <<'PROFILE'

# >>> cloudravel toolchain >>>
export DOTNET_ROOT="${HOME}/.dotnet"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
if [ -s "${HOME}/.nvm/nvm.sh" ]; then
  . "${HOME}/.nvm/nvm.sh" --no-use
  nvm use default >/dev/null 2>&1 || true
  # See the nvm note in .cursor/install.sh: the image's node must not shadow the pinned one.
  [ -n "${NVM_BIN:-}" ] && PATH="${NVM_BIN}:${PATH}"
fi
export PATH="${DOTNET_ROOT}:${HOME}/.local/bin:${PATH}"
# <<< cloudravel toolchain <<<
PROFILE
fi

# ---------------------------------------------------------------------------
# Warm dependency caches so the first build in a session is not a cold restore.
# ---------------------------------------------------------------------------
log "Restoring .NET packages"
(cd "${REPO_ROOT}" && DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 dotnet restore CloudRavel.sln)

log "Installing frontend dependencies"
(cd "${REPO_ROOT}/src/frontend" && npm ci --no-audit --no-fund)

log "Environment ready"
dotnet --version
node --version
terraform version | head -1
helm version --short
