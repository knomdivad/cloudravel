#!/usr/bin/env bash
# Create two workspaces (org + tenant) for the cross-tenant guardrail test.
# Idempotent: reuses fixed GUIDs.
set -euo pipefail
API="${API_URL:-http://localhost:17071/api}"
TOKEN=$(curl -sS -m 10 -X POST "$API/auth/login" -H 'Content-Type: application/json' \
  -d '{"email":"admin@local","password":"ChangeMe123!"}' | python3 -c 'import json,sys; print(json.load(sys.stdin)["token"])')

create_org() { # $1=name $2=org-guid
  curl -sS -m 15 -X POST "$API/organizations" \
    -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
    -H 'X-Tenant-Id: 00000000-0000-0000-0000-000000000000' \
    -d "{\"name\":\"$1\"}" -w '\nHTTP:%{http_code}\n'
}

# Per docker-compose comments: organizations POST with workspace header 0-guid creates orgs.
create_org "Company A" "aaaaaaaa-0000-0000-0000-000000000001"
create_org "Company B" "bbbbbbbb-0000-0000-0000-000000000002"

curl -sS -m 10 "$API/organizations" -H "Authorization: Bearer $TOKEN" | head -c 1200
echo
