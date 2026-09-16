#!/usr/bin/env bash
# Login as admin@local and print auth-probe JSON for the verification script.
set -euo pipefail
API="${API_URL:-http://localhost:17071/api}"
TOKEN=$(curl -sS -m 10 -X POST "$API/auth/login" -H 'Content-Type: application/json' \
  -d '{"email":"admin@local","password":"ChangeMe123!"}' | python3 -c 'import json,sys; print(json.load(sys.stdin)["token"])')
echo "token_len=${#TOKEN}"
curl -sS -m 10 "$API/auth/me" -H "Authorization: Bearer $TOKEN" | head -c 500
echo
