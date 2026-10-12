# Create organization (system admin)

System administrators can create a new organization workspace from the sidebar to begin adding clouds and viewing tenant-scoped pages.

## Sub-features

- `org-open-modal` — Sidebar **+ New** opens **New Organization** dialog.
- `org-create` — Submit **Create Organization** with name and Development/Production label.
- `org-select` — New org becomes selected in the Organization dropdown.

## How to get to it (user POV)

- Sign in as the seeded system admin (`admin@local`).
- In the sidebar organization panel, choose **+ New**.
- Fill **Name**, pick **Development** or **Production**, then **Create Organization**.

## Driving it with control-cloudravel

Preconditions:

- Authenticated as `system_admin` (default seed admin).
- No name collision if re-running; use a unique name like `Verify Org ${VERIFY_RUN_ID}`.

- **Open modal.** Playwright: click button with title **Create a new organization** (`+ New` text). Heading **New Organization** visible.
- **Fill and submit.** Fill the required **Name** textbox (placeholder `Acme Corp`), keep **Development** selected, click **Create Organization**. Modal closes; dropdown lists the new org.
- **Proof.** Screenshot showing organization name in sidebar select; optional `GET ${API_URL}/organizations` with bearer token lists the org.
- **Cleanup note.** Removing test orgs is not automated; prefer disposable Compose volumes via `control-cloudravel cleanup` after proof.

## Gotchas

- Only **system_admin** sees **+ New**; org-only admins use **Organization** page for membership, not global create.
- Environment buttons label the workspace; Development instance still never contacts real clouds (`Platform__Environment`).
- Per-org SSO settings exist in UI but federation is **not enforced** yet (`organization/page.tsx` copy).
