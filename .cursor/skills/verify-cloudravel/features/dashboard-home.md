# Dashboard after login

The Dashboard is the default home route (`/`) after authentication, showing summary cards and charts scoped to the selected organization workspace.

## Sub-features

- `dashboard-nav` — Reach home via sidebar link **Dashboard**.
- `dashboard-empty-org` — Fresh estate with no organizations shows tenant selection prompt on `/`.
- `dashboard-metrics` — Summary cards (Total Resources, Changes, Advisor, Defender, Policy) when a workspace with data is selected.

## How to get to it (user POV)

- Sign in locally, then choose **Dashboard** in the left sidebar (href `/`).
- On a default migration-only estate, the main pane shows **No tenant selected** until an organization exists.

## Driving it with control-cloudravel

Preconditions:

- Completed **local login** (session active in browser context).
- For metric cards, either run `database/seed-demo-data.sql` or create an organization and connect clouds (out of scope for minimal proof).

- **Navigate home.** After login, run Playwright `page.getByRole('link', { name: 'Dashboard' }).click()` or open `${WEB_URL}/`. On fresh seed, text **No tenant selected** is visible.
- **Proof (fresh seed).** Screenshot showing sidebar **Dashboard** active and empty-state copy; ARIA snapshot includes `No tenant selected`.
- **Proof (with org).** Summary card headings include **Total Resources** and **Changes (24h)** (`page.tsx`).

## Gotchas

- `tenantId` comes from the selected organization (`TenantContext`); zero orgs means dashboard empty state even though login succeeded.
- Charts require tenant-scoped API data; do not fail login proof when cards show zero on an empty estate.
- Platform environment badge in the shell reads from `GET /api/platform` (via `api.getPlatformInfo()`).
