-- ============================================================================
-- Migration 008: Organizations (the in-app workspace above clouds)
--
-- Introduces the "Organization" — the workspace an operator selects and adds
-- clouds to. An Organization owns its clouds as peers:
--   * an Azure tenant  → the tenants row whose tenant_id = org_id (+ subscriptions)
--   * AWS Organizations → cloud_orgs (provider 'Aws')  + member accounts
--   * GCP Organizations → cloud_orgs (provider 'Gcp')  + projects
--
-- org_id IS the workspace / RLS boundary value — the same GUID already stored in
-- every tenant_id column — so this is an ownership/naming layer over the existing
-- workspace id, NOT a data-plane rewrite. Adding a cloud never creates an org.
--
-- environment ('Development' | 'Production') labels each workspace. Note the
-- running instance's Platform:Environment setting is what actually GATES
-- inventory collection, so a Development instance never calls a seed org's clouds.
--
-- Run after 001–007. Not RLS-protected: like `tenants`, it is the workspace
-- registry that DEFINES the boundary rather than being filtered by it.
-- ============================================================================

CREATE TABLE organizations (
    org_id      UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    name        NVARCHAR(256)    NOT NULL,
    environment NVARCHAR(20)     NOT NULL DEFAULT 'Development'
                  CHECK (environment IN ('Development', 'Production')),
    status      NVARCHAR(20)     NOT NULL DEFAULT 'active'
                  CHECK (status IN ('active', 'suspended')),
    created_at  DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    created_by  NVARCHAR(256)    NOT NULL DEFAULT 'system'
);
GO

-- Upgrade-in-place: every existing workspace (a tenants row) becomes an
-- Organization with the same id, so nothing needs re-onboarding. A fresh install
-- has no tenants yet at this point; the demo seed inserts its own org row.
INSERT INTO organizations (org_id, name, environment, created_by)
SELECT t.tenant_id, t.display_name, 'Development', 'system'
FROM tenants t
WHERE NOT EXISTS (SELECT 1 FROM organizations o WHERE o.org_id = t.tenant_id);
GO
