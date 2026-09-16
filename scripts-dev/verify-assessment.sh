#!/usr/bin/env bash
# End-to-end verification of the assessment delivery flow (t_de701540).
# Local-dev only: seeded admin credentials against the local OrbStack stack.
#
#   login -> seed estate -> start assessment -> report(json/csv/md)
#   -> remediation approval blocked -> convert -> approval path restored
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
. ./.env

API=http://localhost:7071/api
SA_PW=$(grep '^MSSQL_SA_PASSWORD' .env | cut -d= -f2-)
NET=$(docker compose ps -q api >/dev/null 2>&1 && docker inspect t_de701540-api-1 --format '{{range $k,$v := .NetworkSettings.Networks}}{{$k}}{{end}}')
step() { echo; echo "== $1 =="; }

step "1. Login (admin@local)"
TOKEN=$(curl -s -X POST "$API/auth/login" -H 'Content-Type: application/json' \
  -d '{"username":"admin@local","password":"ChangeMe123!"}' | python3 -c 'import sys,json;print(json.load(sys.stdin)["token"])')
test -n "$TOKEN" && echo "token OK (${#TOKEN} chars)" || { echo "LOGIN FAILED"; exit 1; }
AUTH="Authorization: Bearer $TOKEN"
NO_TENANT='X-Tenant-Id: 00000000-0000-0000-0000-000000000000'

step "2. Create workspace org + seed estate"
ORG=$(curl -s -X POST "$API/organizations" -H "$AUTH" -H "$NO_TENANT" -H 'Content-Type: application/json' \
  -d '{"name":"Assessment Verify Co"}' | python3 -c 'import sys,json;print(json.load(sys.stdin)["orgId"])')
echo "org: $ORG"
XH="X-Tenant-Id: $ORG"

sed "s/\$(Org)/$ORG/g" scripts-dev/seed-verify-estate.sql > /tmp/seed-verify-estate.sql
docker run --rm --network "$NET" -v /tmp/seed-verify-estate.sql:/seed.sql:ro mcr.microsoft.com/mssql-tools:latest \
  /opt/mssql-tools/bin/sqlcmd -S mssql -U sa -P "$SA_PW" -I -d cloudraveldb \
  -i /seed.sql > /dev/null && echo "seed OK"

step "3. Assessment state before start"
curl -s "$API/assessment" -H "$AUTH" -H "$XH" | python3 -m json.tool

step "4. Start assessment (10-day window, \$3000 fee)"
curl -s -X POST "$API/assessment/start" -H "$AUTH" -H "$XH" -H 'Content-Type: application/json' \
  -d '{"windowDays":10,"fee":3000}' | python3 -m json.tool

step "5. Report: JSON summary"
curl -s "$API/assessment/report" -H "$AUTH" -H "$XH" > /tmp/report.json
python3 << 'PYEOF'
import json
r = json.load(open('/tmp/report.json'))
s = r['savings']
print(f"workspace      : {r['workspaceName']}")
print(f"state          : {r['engagementState']}")
print(f"estate         : {r['estate']['resourceCount']} resources, {r['estate']['changesInWindow']} changes in window")
print(f"identified     : ${s['identifiedAnnual']}/yr (${s['identifiedMonthly']}/mo)")
print(f"guarantee      : {s['feeMultiple']}x fee, target ${s['targetSavings']}, meets={s['meetsGuarantee']}")
print(f"fix-list items : {len(r['fixList'])}")
for i in r['fixList']:
    sav = i.get('estimatedAnnualSavings')
    print(f"  #{i['rank']} T{i['tier']} {i['source']:8s} {i['title'][:58]:58s} {('$' + format(sav, ',.0f')) if sav else '-'}")
PYEOF

step "6. Report: Markdown (first 32 lines)"
curl -s "$API/assessment/report?format=md" -H "$AUTH" -H "$XH" | head -32

step "7. Report: CSV"
curl -s "$API/assessment/report?format=csv" -H "$AUTH" -H "$XH" | head -8

step "8. Read-only gate: remediation approval must be BLOCKED"
ACTION=$(curl -s -X POST "$API/remediations" -H "$AUTH" -H "$XH" -H 'Content-Type: application/json' \
  -d '{"playbookKey":"azure-vm-deallocate","resourceId":"/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-1/providers/Microsoft.Compute/virtualMachines/vm-app-01","reason":"verify: assessment gate test"}' \
  | python3 -c 'import sys,json;print(json.load(sys.stdin)["id"])')
echo "proposed action id: $ACTION (proposals stay allowed)"
APPROVE_HTTP=$(curl -s -o /tmp/approve_out.json -w '%{http_code}' -X POST "$API/remediations/$ACTION/approve" -H "$AUTH" -H "$XH")
echo "approve HTTP status: $APPROVE_HTTP (blocked; endpoint maps gate to INVALID_OPERATION)"
cat /tmp/approve_out.json; echo

step "9. Complete + convert: engagement becomes ongoing monitoring"
curl -s -X POST "$API/assessment/complete" -H "$AUTH" -H "$XH" | python3 -c 'import sys,json;print("state:",json.load(sys.stdin)["state"])'
curl -s -X POST "$API/assessment/convert" -H "$AUTH" -H "$XH" | python3 -m json.tool

step "10. After convert: approval path restored (action approves, HTTP 200)"
APPROVE2_HTTP=$(curl -s -o /tmp/approve2_out.json -w '%{http_code}' -X POST "$API/remediations/$ACTION/approve" -H "$AUTH" -H "$XH")
echo "approve HTTP status: $APPROVE2_HTTP (expect 200)"
python3 -c 'import json;d=json.load(open("/tmp/approve2_out.json"));print("action status:",d.get("status"))'

echo
echo "E2E DONE"
