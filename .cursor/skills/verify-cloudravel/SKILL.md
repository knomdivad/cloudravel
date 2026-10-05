---
name: verify-cloudravel
description: "Drive the CloudRavel local Docker Compose stack (Next.js UI + Azure Functions API) the way an operator does: launch with make/compose, doctor health, sign in as the seeded local admin, and capture proof artifacts. Use when verifying UI routes, local auth, inventory/ops pages, or API health on knomdivad/cloudravel."
---

# Verify CloudRavel

CloudRavel’s primary verification surface is the **local Docker Compose stack** documented in the repo root `README.md` and `Makefile`: web UI at `http://localhost:3000`, API at `http://localhost:7071/api`, seeded local system admin **`admin@local` / `ChangeMe123!`** (local dev only; hash is in source). Entra ID SSO is optional when `NEXT_PUBLIC_AZURE_AD_*` is unset; agents should use **local email/password login** unless Entra env vars are configured.

Secondary surfaces: raw HTTP to the API (health, login, authenticated routes) and existing repo scripts under `verify/` (API-only probes). There is **no Playwright/Cypress suite in-tree**; this skill ships **`control-cloudravel`** (Compose lifecycle + curl + Playwright login drive).

Read `.cursor/skills/verify-cloudravel/features/README.md` before driving a feature; each feature file is the recipe for one user path.

## Launch

From the repository root, with Docker available:

1. Ensure `.env` exists (`cp .env.example .env` — `MSSQL_SA_PASSWORD` and `LOCAL_AUTH_JWT_SIGNING_KEY` have working defaults in `.env.example`).
2. Put helpers on `PATH` or invoke by path:

```bash
export PATH="/workspace/.cursor/skills/verify-cloudravel/scripts:${PATH}"
export VERIFY_RUN_ID="${VERIFY_RUN_ID:-$(openssl rand -hex 4)}"
control-cloudravel launch
```

`launch` sets an isolated Compose project name `cloudravel-verify-<VERIFY_RUN_ID>`, writes state to `/opt/cursor/artifacts/verify-cloudravel/state/run.env`, and runs `docker compose up -d --build` (equivalent to `make up`). Override host ports if needed: `WEB_HOST_PORT`, `API_HOST_PORT`.

**Ready when:**

- `control-cloudravel doctor` exits 0.
- `GET ${API_URL}/health` returns JSON with `"status":"healthy"` and database check healthy.
- `GET ${WEB_URL}/` returns HTTP 200 (nginx serving the Next.js static export).

First boot runs the `migrator` service once (schema + bootstrap admin only; **no demo orgs** unless you manually run `database/seed-demo-data.sql`).

**Teardown (see Cleanup):** `control-cloudravel cleanup` — only the project named in `run.env`.

Plain Compose without the helper (same stack):

```bash
make up    # docker compose up -d --build
make down  # docker compose down
```

**Cloud Agent / workspace mount:** `docker compose build` may fail on `COPY` with `invalid argument`. Use `.cursor/skills/verify-cloudravel/scripts/build-images-via-tmp.sh` (extracts sources to `/tmp` first), then `docker compose up -d` without `--build`. If image **pull** fails with overlay whiteout errors on `azure-sql-edge`, the VM cannot run the stack; prove on a host where `docker pull mcr.microsoft.com/azure-sql-edge` succeeds. Fallback when images build but pull works: `.cursor/skills/verify-cloudravel/scripts/prove-local-login.sh` (Compose deps + host `dotnet run` / `npm run dev`); it records PIDs and tears down only what it started.

## Doctor

Run after launch and whenever behavior looks wrong:

```bash
control-cloudravel doctor
```

Doctor is read-only. It checks:

- The recorded `COMPOSE_PROJECT_NAME` exists (`docker compose ps`).
- `${API_URL}/health` and `${API_URL}/health/ready` succeed.
- `${WEB_URL}/` responds HTTP 200.

Optional API identity probe (does not replace UI login proof):

```bash
control-cloudravel api-login
```

Expect `token_len` > 0 and `/auth/me` JSON including the admin email.

Do **not** drive a Compose project you did not start in this run unless you intentionally attach to a developer stack and accept shared state.

## Drive

**Harness:** `control-cloudravel` + Playwright (via `npm ci` in `.cursor/skills/verify-cloudravel/` for browser commands).

Install browser deps once per VM:

```bash
cd .cursor/skills/verify-cloudravel && npm ci --no-audit --no-fund && npx playwright install chromium
```

**UI (preferred for login and pages):**

- Base URL: `WEB_URL` from `run.env` (default `http://127.0.0.1:3000`).
- Login gate (`src/frontend/src/app/layout.tsx`): Email textbox (labeled `Email`), password field, button **`Sign in`**. Email field is `type="text"` so `admin@local` validates.
- After login, sidebar nav links include **`Dashboard`**, **`Inventory`**, **`Admin`** (system admin), etc. Routes live under `src/frontend/src/app/`.

Example — feature **`local-login`** (see `features/local-login.md`):

```bash
control-cloudravel browser-login
```

**HTTP (API-only features):**

```bash
curl -sf "${API_URL}/health" | jq .
curl -sf "${API_URL}/health/ready" | jq .
```

Authenticated calls: obtain a JWT via `POST ${API_URL}/auth/login` with JSON `{"email":"admin@local","password":"ChangeMe123!"}` (see `verify/login-probe.sh`).

**Fresh estate note:** With default migrations only, there are **zero organizations**; Dashboard and Inventory show empty-state copy until an org exists (system admin can click **`+ New`** in the sidebar → **`New Organization`** modal → **`Create Organization`**).

## Evidence

Proof artifacts go under:

```text
/opt/cursor/artifacts/verify-cloudravel/<VERIFY_RUN_ID>/<feature-id>/
```

Standards:

- Exercise the **real user path** (browser login for auth; public health endpoints for API health).
- Capture **action + resulting state** (post-login screenshot and ARIA snapshot, not only the login form).
- Record `feature` id and entry point in a small `proof.json` when using the browser helper.
- API proofs: save curl command, HTTP status, and response body (truncate secrets; JWTs are session artifacts, not committed).
- Do not treat a skipped map entry as verified via another route.

`control-cloudravel browser-login` writes `post-login.png`, `post-login.aria.txt`, and `proof.json` under `.../local-login/`.

## Cleanup

Only tear down what this run started:

```bash
control-cloudravel cleanup
```

This runs `docker compose down` for the **`COMPOSE_PROJECT_NAME`** stored in `/opt/cursor/artifacts/verify-cloudravel/state/run.env`. It does **not** delete proof under `/opt/cursor/artifacts/verify-cloudravel/`. Never `pkill` by process name; never `docker kill` unrelated containers.

After cleanup, confirm evidence still exists:

```bash
test -f "/opt/cursor/artifacts/verify-cloudravel/${VERIFY_RUN_ID}/local-login/post-login.png"
```

## Helpers

| Helper | Purpose |
|--------|---------|
| `.cursor/skills/verify-cloudravel/scripts/control-cloudravel` | `launch`, `doctor`, `api-login`, `browser-login`, `cleanup` |
| `.cursor/skills/verify-cloudravel/scripts/browser-login.mjs` | Playwright local login + artifacts |
| Repo `verify/login-probe.sh` | Quick API login + `/auth/me` (optional; uses `API_URL`) |
| Repo `Makefile` | `make up`, `make down`, `make ps`, `make logs` |

Example end-to-end proof (one feature):

```bash
export VERIFY_RUN_ID="proof1"
export PATH="/workspace/.cursor/skills/verify-cloudravel/scripts:$PATH"
control-cloudravel launch
control-cloudravel doctor
control-cloudravel browser-login
control-cloudravel cleanup
ls -la "/opt/cursor/artifacts/verify-cloudravel/${VERIFY_RUN_ID}/local-login/"
```

For keeping the feature map current as routes change, use `/maintain-verification-skill` when available.
