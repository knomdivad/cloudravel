#!/usr/bin/env bash
# Start Docker for CloudRavel verification (Compose stack). Idempotent on agent boot.
set -euo pipefail

if ! command -v dockerd >/dev/null 2>&1; then
  exit 0
fi

if docker info >/dev/null 2>&1 || sudo docker info >/dev/null 2>&1; then
  exit 0
fi

mkdir -p /tmp/docker
nohup sudo dockerd --data-root /tmp/docker > /tmp/dockerd.log 2>&1 &
for _ in $(seq 1 30); do
  if sudo docker info >/dev/null 2>&1; then
    exit 0
  fi
  sleep 1
done
echo "dockerd did not become ready; see /tmp/dockerd.log" >&2
exit 1
