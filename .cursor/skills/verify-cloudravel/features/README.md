# CloudRavel verification map

This directory is the maintained source for verifying user-facing behavior of CloudRavel on the **local Docker Compose stack**. Read this index before driving the app, then open the feature file that matches the path you need to prove.

## Baseline preconditions

- Launch the stack with `control-cloudravel launch` (or `make up` from the repo root) using an isolated `COMPOSE_PROJECT_NAME` such as `cloudravel-verify-<VERIFY_RUN_ID>`.
- Ensure `.env` exists (`cp .env.example .env`); defaults include `MSSQL_SA_PASSWORD` and `LOCAL_AUTH_JWT_SIGNING_KEY`.
- Web UI at `WEB_URL` (default `http://127.0.0.1:3000`); API at `API_URL` (default `http://127.0.0.1:7071/api`).
- Seeded local system admin: **`admin@local` / `ChangeMe123!`** (documented in `README.md`; local dev only).
- Run `control-cloudravel doctor` and require healthy API, ready probe, and web HTTP 200.
- Install Playwright once: `cd .cursor/skills/verify-cloudravel && npm ci && npx playwright install chromium`.
- Never drive a Compose project you did not start for this verification run unless you explicitly accept shared local state.

## Driving conventions

- Start from the baseline unless a feature’s preconditions say otherwise.
- Prefer **roles and accessible names** from the login gate and sidebar (`Sign in`, `Dashboard`, `Inventory`, `Admin`).
- Use **route paths** under `src/frontend/src/app/` when deep-linking (`/inventory`, `/admin`, `/organization`).
- Browser steps: `control-cloudravel browser-login` or Playwright recipes in each feature file.
- API steps: `curl` against `${API_URL}`; login body uses `"email"` (see `verify/login-probe.sh`).
- Cleanup removes only the recorded Compose project; **do not delete** `/opt/cursor/artifacts/verify-cloudravel/`.

## Proof and skip reporting

- Capture the user action and resulting state, not only the final screen.
- UI proof: screenshot plus ARIA snapshot with app identity visible (`CloudRavel` heading or sidebar).
- API proof: HTTP status, response body, and command transcript.
- Record feature id and entry point with every artifact (`proof.json` or filename prefix).
- Report unreachable paths with the attempted step and unmet precondition (e.g. inventory with no organization selected on a fresh estate).
- Do not mark a map entry verified by driving a different feature.

## Feature entry contract

Each feature file starts with an H1 title and one paragraph describing user-visible behavior. It then uses exactly four H2 sections in this order:

1. `Sub-features`
2. `How to get to it (user POV)`
3. `Driving it with control-cloudravel`
4. `Gotchas`

## Features

- [Local login (admin@local)](./local-login.md) — email/password sign-in through the login gate and authenticated shell.
- [API health and readiness](./api-health.md) — anonymous `/health` and `/health/ready` probes.
- [Dashboard after login](./dashboard-home.md) — home route `/` and empty-state when no organization exists.
- [Inventory explorer](./inventory-browse.md) — `/inventory` list and filters when a workspace is selected.
- [Create organization (system admin)](./organization-create.md) — sidebar `+ New` → `New Organization` modal on a fresh estate.
