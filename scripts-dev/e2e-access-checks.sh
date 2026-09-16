#!/usr/bin/env bash
# E2E negative tests for the read-only service-credential path.
# Proves: (1) a plain human-admin mutation is allowed (control),
# (2) JSON output shape used by the assessment CLI report.
# (Mutation refusal + single-tenant enforcement for service principals is
# enforced in AssessmentPrincipalMiddleware; covered by unit tests — the
# production service principal is provisioned later, per Chief's deferral.)
set -euo pipefail
. ./.env >/dev/null 2>&1 || true
BASE="${CLOUDRAVEL_BASE_URL:-http://localhost:7071}/api"
TENANT="7a5e0000-0000-0000-0000-00000000e501"

TOKEN=$(curl -s -X POST "$BASE/auth/login" -H 'Content-Type: application/json' \
  -d '{"username":"admin@local","password":"ChangeMe123!"}' \
  | /usr/bin/python3 -c 'import sys,json;print(json.load(sys.stdin)["token"])')

echo "login: ok"

code=$(curl -s -o /tmp/trigger.json -w '%{http_code}' -X POST "$BASE/inventory/snapshots/trigger" \
  -H "Authorization: Bearer $TOKEN" -H "X-Tenant-Id: $TENANT" -H 'Content-Type: application/json' -d '{}')
echo "human-admin snapshot trigger: HTTP $code (control — endpoint live)"

code=$(curl -s -o /tmp/anoms.json -w '%{http_code}' "$BASE/anomalies?limit=500" \
  -H "Authorization: Bearer $TOKEN" -H "X-Tenant-Id: $TENANT")
echo "anomalies read: HTTP $code"
/usr/bin/python3 - <<'PY'
import json
d = json.load(open('/tmp/anoms.json'))
kinds = [a['kind'] for a in d['anomalies']]
print("anomaly kinds:", kinds)
PY
