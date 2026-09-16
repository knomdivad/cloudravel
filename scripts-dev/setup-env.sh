#!/usr/bin/env bash
# One-shot local stack bootstrap for verifying t_de701540.
# Copies .env.example -> .env if missing (local-dev defaults only).
set -euo pipefail
# Repo root = two levels up from scripts-dev/
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
if [ ! -f .env ]; then
  cp .env.example .env
  echo "created .env from example"
else
  echo ".env already present"
fi
