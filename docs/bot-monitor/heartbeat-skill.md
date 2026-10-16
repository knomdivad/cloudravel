# BotMonitor heartbeat skill

We use this routine so each bot reports status to CloudRavel without exposing secrets in logs.

## Credentials

Preferred: mode `0600` files (never commit these paths or contents):

- `CR_BOT_TOKEN_FILE` path to the bearer token (shown once at registration)
- `CR_BOT_HMAC_SECRET_FILE` path to the base64 HMAC secret (shown once at registration)

Alternative: `CR_BOT_TOKEN` and `CR_BOT_HMAC_SECRET` environment variables for local runs only.

Optional: `CR_API_BASE` API root, default `http://localhost:7071/api`

Optional health metrics (bounded JSON object, serialized cap about 3500 bytes on the server):

- `CR_BOT_METRICS_FILE` path to a JSON file (mode `0600`) with a `metrics` object body fragment
- `CR_BOT_METRICS` inline JSON for local runs only

Example metrics object (all fields optional):

```json
{
  "routine": { "id": "weekly-keep", "expectedAt": "2026-10-16T12:00:00Z", "ranAt": "2026-10-16T12:05:00Z", "result": "ok" },
  "connectors": [{ "name": "gmail", "state": "ok" }],
  "actions": { "attempted": 12, "failed": 1 },
  "inbox": { "unanswered": 3, "oldestMinutes": 45 },
  "approvals": { "pending": 1, "oldestMinutes": 120 },
  "queue": { "open": 5, "oldestMinutes": 30 },
  "escalations": 0
}
```

The API stores the sanitized object on each heartbeat. CloudRavel derives consecutive failure streaks, routine miss flags, check in lateness, runs per day, and action error counts for dashboards.

## Keep check prompt

Before posting, run your local Keep checklist (disk, queue depth, last job error). Summarize results into:

- `keep_check`: `pass`, `warn`, `fail`, or `none`
- `keep_check_detail`: short text, at most 1000 characters

If checks fail, still post a heartbeat with `status` `error` so operators see the drift.

## Script

`scripts/bot-monitor/send-heartbeat.py` (Python 3, stdlib `hmac` and `hashlib` only)

The script reads credentials from files or the environment, signs the JSON body, and POSTs once. It exits 0 on HTTP 200 and fails soft (exit 1 with a short message) on auth or network errors.

```bash
python3 scripts/bot-monitor/send-heartbeat.py
```

## Registry

Link an organization before creating bots: `POST /api/bot-monitor/registry/tenants` with JSON `{"organizationId":"<org guid>"}` (system admin). Bot create uses the same id as `tenantId`.

## API contract

`POST /api/bot-monitor/heartbeat`

Headers:

- `Authorization: Bearer <token>`
- `X-CR-Timestamp` unix seconds UTC
- `X-CR-Signature` lowercase hex HMAC-SHA256 over `timestamp + "." + raw body`

Body JSON example:

```json
{
  "status": "ok",
  "lastTask": "Keep weekly checks",
  "errors": [],
  "keepCheck": "pass",
  "keepCheckDetail": "All probes succeeded",
  "metrics": {
    "inbox": { "unanswered": 0, "oldestMinutes": 0 },
    "actions": { "attempted": 4, "failed": 0 }
  }
}
```
