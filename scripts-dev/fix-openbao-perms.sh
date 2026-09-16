#!/usr/bin/env bash
# Fix ownership of the OpenBao data volume so the container's openbao user
# (uid 100, gid 1000) can write its file backend. One-shot fix for a fresh
# OrbStack volume that came up root-owned.
set -euo pipefail
VOLUME="${1:-t_de701540_openbao-data}"
docker run --rm -v "$VOLUME:/openbao/data" alpine chown 100:1000 /openbao/data
docker run --rm -v "$VOLUME:/openbao/data" alpine stat -c '%U %G %a' /openbao/data
