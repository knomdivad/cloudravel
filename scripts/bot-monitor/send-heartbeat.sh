#!/usr/bin/env bash
set -euo pipefail

API_BASE="${CR_API_BASE:-http://localhost:7071/api}"
TOKEN="${CR_BOT_TOKEN:?CR_BOT_TOKEN is required}"
SECRET_B64="${CR_BOT_HMAC_SECRET:?CR_BOT_HMAC_SECRET is required}"

KEEP="${CR_KEEP_CHECK:-pass}"
DETAIL="${CR_KEEP_DETAIL:-Routine Keep checks completed}"
STATUS="${CR_BOT_STATUS:-ok}"
TASK="${CR_BOT_TASK:-Keep health routine}"

BODY=$(printf '{"status":"%s","lastTask":"%s","errors":[],"keepCheck":"%s","keepCheckDetail":"%s"}' \
  "$STATUS" "$TASK" "$KEEP" "$DETAIL")

TS=$(date -u +%s)
CANON="${TS}.${BODY}"

WORKDIR=
cleanup() {
  if [ -n "${WORKDIR:-}" ] && [ -d "$WORKDIR" ]; then
    rm -rf "$WORKDIR"
  fi
}
trap cleanup EXIT

umask 077
WORKDIR=$(mktemp -d)
KEY_BIN="$WORKDIR/key.bin"
KEY_HEX="$WORKDIR/key.hex"

printf '%s' "$SECRET_B64" | base64 -d >"$KEY_BIN"
xxd -p -c 256 "$KEY_BIN" | tr -d '\n' >"$KEY_HEX"

SIG=$(
  printf '%s' "$CANON" | openssl dgst -sha256 -mac HMAC -macopt "hexkey:$(cat "$KEY_HEX")" -hex \
    | awk '{print $2}'
)

HTTP=$(curl -sS -o /tmp/cr-hb.out -w '%{http_code}' \
  -X POST "${API_BASE}/bot-monitor/heartbeat" \
  -H "Authorization: Bearer ${TOKEN}" \
  -H "Content-Type: application/json" \
  -H "X-CR-Timestamp: ${TS}" \
  -H "X-CR-Signature: ${SIG}" \
  --data-binary "$BODY")

if [ "$HTTP" = "200" ]; then
  echo "Heartbeat accepted"
  exit 0
fi

echo "Heartbeat failed (HTTP ${HTTP})" >&2
head -c 200 /tmp/cr-hb.out >&2 || true
exit 1
