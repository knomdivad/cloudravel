# BotMonitor heartbeat skill

We use this routine so each bot reports status to CloudRavel without exposing secrets in logs.

## Environment

Set these variables before running the helper scripts (never commit real values):

- `CR_BOT_TOKEN` per-bot bearer token (shown once at registration)
- `CR_BOT_HMAC_SECRET` base64 HMAC secret (shown once at registration)
- `CR_API_BASE` optional API root, default `http://localhost:7071/api`

## Keep check prompt

Before posting, run your local Keep checklist (disk, queue depth, last job error). Summarize results into:

- `keep_check`: `pass`, `warn`, `fail`, or `none`
- `keep_check_detail`: short text, at most 1000 characters

If checks fail, still post a heartbeat with `status` `error` so operators see the drift.

## Scripts

- `scripts/bot-monitor/send-heartbeat.sh` (bash, curl, openssl)
- `scripts/bot-monitor/send-heartbeat.py` (Python 3)

Both read token and secret from the environment, sign the payload, and POST once. The bash helper decodes the base64 HMAC key into a private temp file, derives a hex key, and passes it to OpenSSL with `-mac HMAC -macopt hexkey:...` so the raw secret never appears on the command line. They exit 0 on HTTP 200 and fail soft (exit 1 with a short message) on auth or network errors.

## API contract

`POST /api/bot-monitor/heartbeat`

Headers:

- `Authorization: Bearer <CR_BOT_TOKEN>`
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
