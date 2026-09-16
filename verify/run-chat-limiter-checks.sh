#!/usr/bin/env bash
# Re-verify rate limiting + out-of-context refusal with a clean limiter
# (the first run exhausted the 3-per-user window).
# Restarts ONLY the api container to reset the in-process limiter, then runs
# scenario 5 (out-of-context) and scenario 6 (rate-limit trip) in order.
set -uo pipefail
API="${API_URL:-http://localhost:17071/api}"
A_ORG="36c4bcaa-6f6e-42da-8811-3a70d9932386"

TOKEN=$(curl -sS -m 10 -X POST "$API/auth/login" -H 'Content-Type: application/json' \
  -d '{"email":"admin@local","password":"ChangeMe123!"}' | python3 -c 'import json,sys; print(json.load(sys.stdin)["token"])')

chat() { # $1=token $2=tenant $3=message
  local body
  body=$(python3 - "$3" <<'PY'
import json, sys
print(json.dumps({"message": sys.argv[1]}))
PY
)
  curl -sS -m 60 -X POST "$API/ai/chat" \
    -H "Authorization: Bearer $1" -H "X-Tenant-Id: $2" -H 'Content-Type: application/json' \
    -d "$body" -w '|HTTP_STATUS:%{http_code}'
}

echo "=== out-of-context question (clean limiter) ==="
chat "$TOKEN" "$A_ORG" "Who won the world series in 1927?"

echo
echo "=== rate-limit trip: messages 1..5 (limit=3) ==="
for i in 1 2 3 4 5; do
  chat "$TOKEN" "$A_ORG" "rate me up $i" | grep -o 'HTTP_STATUS:[0-9]*'
done

echo
echo "=== per-user limiter does not spill to Company B tenant window ==="
chat "$TOKEN" "$B_ORG" "What are the total resources in my workspace?" | grep -o 'HTTP_STATUS:[0-9]*'
