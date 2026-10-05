# Inventory explorer

Inventory explorer lists cloud resources for the selected organization workspace, with filters and pagination.

## Sub-features

- `inventory-nav` — Sidebar **Inventory** → `/inventory`.
- `inventory-empty-tenant` — Message when no workspace is selected.
- `inventory-table` — Resource table when data exists (50 per page).

## How to get to it (user POV)

- Sign in, select an organization in the sidebar switcher (if any), then choose **Inventory**.
- Direct URL: `/inventory` (requires authenticated session).

## Driving it with control-cloudravel

Preconditions:

- Authenticated browser session.
- An active organization selected (`tenantId` set). On default seed, expect empty state **Select a tenant to view inventory.** until an org is created or demo seed is loaded.

- **Open explorer.** Run Playwright: `page.getByRole('link', { name: 'Inventory' }).click()`. Heading **Inventory Explorer** when tenant selected; otherwise empty-state message.
- **Filters.** With data, use labeled filters (resource type, provider, search). Client-side search filters loaded page (`inventory/page.tsx`).
- **Proof.** Screenshot of heading **Inventory Explorer** or empty-state copy; optional API cross-check `GET ${API_URL}/inventory/resources` with bearer token from login.

## Gotchas

- `Platform__Environment=Development` disables live cloud collection; inventory may be empty even with connected clouds until snapshots exist.
- Demo inventory requires `database/seed-demo-data.sql` or production collection — do not invent resource rows.
- Pagination uses `PAGE_SIZE = 50`; proof on large estates needs explicit page navigation.
