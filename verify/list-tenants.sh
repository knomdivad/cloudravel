#!/usr/bin/env bash
# List workspaces (tenants) visible to admin@local, so the guardrail script
# can be pointed at two of them.
set -euo pipefail
API="${API_URL:-http://localhost:17071/api}"
TOKEN=$(curl -sS -m 10 -X POST "$API/auth/login" -H 'Content-Type: application/json' \
  -d '{"email":"admin@local","password":"ChangeMe123!"}' | python3 -c 'import json,sys; print(json.load(sys.stdin)["token"])')
curl -sS -m 10 "$API/tenants" -H "Authorization: Bearer $TOKEN" | head -c 1500
echo
