#!/usr/bin/env bash
#
# Per-boot startup for the CloudRavel local stack inside a Cloud Agent VM.
#
# A Cloud Agent VM is a nested container, which breaks three things Docker
# normally handles for you. This script reconciles all of them and then brings
# the docker-compose stack up. Every step is idempotent, so it is safe to run
# on every boot and to re-run by hand.
#
#   1. No init system starts dockerd, so we launch it ourselves.
#   2. Bridged (container<->container) frames are dropped unless they bypass
#      iptables; the compose network is unusable without this.
#   3. Docker writes its rules to nftables, but the legacy iptables FORWARD
#      chain still has policy DROP with no rule for compose bridges, so
#      container->internet traffic (image pulls, Alpine apk, AI endpoints) is
#      dropped before it is ever NAT'd.
#
# Result once healthy:  Web http://localhost:3000   API http://localhost:7071
# Seeded local admin:   admin@local / ChangeMe123!
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

log() { printf '\n=== %s\n' "$1"; }

# ---------------------------------------------------------------------------
# 1. Netfilter fixes for Docker networking in a nested container.
# ---------------------------------------------------------------------------
log "Applying nested-container network fixes"
# Bridged frames must skip iptables or intra-stack traffic on the compose
# bridge is dropped by the legacy FORWARD policy below.
sudo sysctl -w net.bridge.bridge-nf-call-iptables=0  >/dev/null 2>&1 || true
sudo sysctl -w net.bridge.bridge-nf-call-ip6tables=0 >/dev/null 2>&1 || true
# Let routed (container->internet) traffic through the legacy FORWARD chain so
# it reaches Docker's nftables MASQUERADE rule instead of being dropped.
sudo iptables-legacy -P FORWARD ACCEPT 2>/dev/null || true

# ---------------------------------------------------------------------------
# 2. Docker daemon.
# ---------------------------------------------------------------------------
log "Docker daemon"
if ! sudo docker info >/dev/null 2>&1; then
  sudo mkdir -p /var/lib/docker
  # setsid detaches dockerd so it survives this script exiting.
  sudo setsid sh -c 'dockerd >/var/log/dockerd.log 2>&1 &'
  for _ in $(seq 1 60); do
    sudo docker info >/dev/null 2>&1 && break
    sleep 1
  done
fi
sudo docker info >/dev/null 2>&1 || { echo "dockerd failed to start; see /var/log/dockerd.log" >&2; exit 1; }
# Non-root socket access for the agent user (group set at install time).
sudo chgrp docker /var/run/docker.sock 2>/dev/null || true
sudo chmod 660    /var/run/docker.sock 2>/dev/null || true
echo "Docker is up: $(sudo docker --version)"

# ---------------------------------------------------------------------------
# 3. Local stack configuration (.env is gitignored — create it once).
# ---------------------------------------------------------------------------
if [[ ! -f .env ]]; then
  log "Creating .env from .env.example"
  cp .env.example .env
  sed -i 's/^MSSQL_SA_PASSWORD=.*/MSSQL_SA_PASSWORD=CloudRavel_Dev_Passw0rd!/' .env
  sed -i 's#^LOCAL_AUTH_JWT_SIGNING_KEY=.*#LOCAL_AUTH_JWT_SIGNING_KEY=dev-local-signing-key-change-me-0123456789#' .env
  grep -q '^COMPOSE_PROJECT_NAME=' .env || echo 'COMPOSE_PROJECT_NAME=cloudravel' >> .env
fi
set -a; . ./.env; set +a
PROJECT="${COMPOSE_PROJECT_NAME:-cloudravel}"

# ---------------------------------------------------------------------------
# 4. OpenBao data volume ownership.
# OpenBao runs as uid 100, but under fuse-overlayfs a fresh named volume is
# root-owned (the image-directory ownership copy that overlay2 performs is
# skipped), so file-mode init fails with "permission denied". Pre-own it.
# ---------------------------------------------------------------------------
log "Preparing OpenBao data volume"
sudo docker volume create "${PROJECT}_openbao-data" >/dev/null
sudo docker run --rm --user 0 -v "${PROJECT}_openbao-data:/data" \
  docker.io/openbao/openbao:latest chown -R 100:1000 /data >/dev/null 2>&1 || true

# ---------------------------------------------------------------------------
# 5. Bring the stack up.
# ---------------------------------------------------------------------------
log "Starting CloudRavel stack (docker compose up -d --build)"
export GIT_SHA="$(git rev-parse --short HEAD 2>/dev/null || true)"
sudo -E docker compose up -d --build

# ---------------------------------------------------------------------------
# 6. Wait for the API to report healthy.
# ---------------------------------------------------------------------------
log "Waiting for API health"
for _ in $(seq 1 60); do
  if curl -fsS -m 5 http://localhost:7071/api/health >/dev/null 2>&1; then
    echo "API healthy."
    break
  fi
  sleep 3
done
curl -fsS -m 5 http://localhost:7071/api/health || true
echo
echo "CloudRavel is up — Web http://localhost:3000  |  API http://localhost:7071/api/health"
