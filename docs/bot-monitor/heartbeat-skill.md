# BotMonitor heartbeat skill

We use this routine so each bot reports status to CloudRavel without exposing secrets in logs.

## Credentials

Preferred: mode `0600` files (never commit these paths or contents):

- `CR_BOT_TOKEN_FILE` path to the bearer token (shown once at registration)
- `CR_BOT_HMAC_SECRET_FILE` path to the base64 HMAC secret (shown once at registration)

Alternative: `CR_BOT_TOKEN` and `CR_BOT_HMAC_SECRET` environment variables for local runs only.

Optional: `CR_API_BASE` API root, default `http://localhost:7071/api`

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
  "keepCheckDetail": "All probes succeeded"
}
```
