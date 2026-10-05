# Local login (admin@local)

Local login lets an operator sign in with email and password when Entra ID is not configured (or alongside it), using the seeded system admin on a fresh local stack.

## Sub-features

- `login-form` — Email and password fields and the `Sign in` button on the login gate.
- `login-success` — Authenticated app shell with sidebar navigation.
- `login-api` — Same identity via `POST /api/auth/login` and `GET /api/auth/me`.

## How to get to it (user POV)

- Open the web UI root (`/`). When unauthenticated, the login gate is shown automatically (`AuthGate` in `layout.tsx`).
- Enter **`admin@local`** in the Email field and the seeded password, then choose **`Sign in`**.
- Alternatively, call the API login endpoint with the same credentials (no browser).

## Driving it with control-cloudravel

Preconditions:

- `control-cloudravel doctor` passes for this run’s `WEB_URL` and `API_URL`.
- Entra-only builds are not required; local login works with blank `AZURE_AD_*` in `.env`.

- **Open login gate.** Navigate to `${WEB_URL}/`. Run `control-cloudravel browser-login` (or Playwright: `page.goto(WEB_URL)`). Heading **`CloudRavel`** and button **`Sign in`** are visible.
- **Submit credentials.** Fill Email with `admin@local`, password with `ChangeMe123!`, click **`Sign in`**. Sidebar link **`Dashboard`** appears; user menu shows the admin display name.
- **API parity.** Run `control-cloudravel api-login`. Exit code 0; stdout includes `token_len=` > 0 and `/auth/me` JSON with email `admin@local`.
- **Proof.** Artifacts under `/opt/cursor/artifacts/verify-cloudravel/<VERIFY_RUN_ID>/local-login/`: `post-login.png`, `post-login.aria.txt`, `proof.json` with `"feature":"local-login"`.

## Gotchas

- The Email input is `type="text"` (not `type="email"`) so `admin@local` is accepted — do not use HTML5 email validation in harnesses.
- Password is the documented dev seed only; never commit or paste production secrets into the skill.
- JWTs live in `sessionStorage` under key `cloudravel-local-token`; a new browser context starts logged out.
- Failed login shows red text from `loginError` on the form; proof requires reaching the authenticated shell.
