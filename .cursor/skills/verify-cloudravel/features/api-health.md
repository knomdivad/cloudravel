# API health and readiness

Health endpoints let operators and load balancers confirm the Functions API and database are up without authentication.

## Sub-features

- `health-summary` — `GET /api/health` overall status and component checks.
- `health-ready` — `GET /api/health/ready` database readiness (`tenants` table reachable).

## How to get to it (user POV)

- Not a UI feature; operators hit the API directly (browser devtools, curl, or monitoring).
- Default local URL: `http://localhost:7071/api/health` and `/api/health/ready` (host port from `API_HOST_PORT`).

## Driving it with control-cloudravel

Preconditions:

- Stack launched; migrator completed successfully.
- `API_URL` from `run.env` (includes `/api` prefix).

- **Summary health.** Run `curl -sf "${API_URL}/health"`. Response JSON has `"status":"healthy"`, `"checks"."database"."status":"healthy"`, and `"configuration"` noting Local auth when Entra is unset.
- **Readiness.** Run `curl -sf "${API_URL}/health/ready"`. Response `"status":"ready"`.
- **Doctor bundles checks.** Run `control-cloudravel doctor`; it calls both endpoints and prints version/commit from health JSON.
- **Proof.** Save bodies to `/opt/cursor/artifacts/verify-cloudravel/<VERIFY_RUN_ID>/api-health/health.json` and `ready.json` plus the curl commands used.

## Gotchas

- Health routes bypass auth middleware (`/health`, `/auth/login` only among anonymous API paths).
- Unhealthy database usually means SQL is still starting or migrator failed — inspect `docker compose logs migrator api`.
- Web UI health is not the same as API health; nginx can serve 200 while API is down — always probe `${API_URL}/health`.
