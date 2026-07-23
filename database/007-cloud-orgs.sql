-- ============================================================================
-- Migration 007: Cloud organizations (provider-agnostic peer grouping)
--
-- Makes AWS accounts and GCP projects first-class peers of Azure rather than
-- children of an Azure tenant. Introduces cloud_orgs — a top-level grouping
-- (an Azure tenant, an AWS Organization, or a GCP Organization/folder) — and
-- attaches cloud_accounts (AWS accounts / GCP projects) to an org instead of
-- to an Azure tenant. tenant_id remains only as the workspace / RLS boundary
-- (the enterprise running the platform), NOT as an Azure-specific dependency.
--
-- Run after 001–006. Sets bypass_rls so the backfill can read/write the
-- RLS-protected cloud_accounts / cloud_orgs tables.
-- ============================================================================

EXEC sp_set_session_context @key = N'bypass_rls', @value = 1;
GO

-- Top-level, provider-agnostic organization. Peers across providers.
CREATE TABLE cloud_orgs (
    org_id      UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    tenant_id   UNIQUEIDENTIFIER NOT NULL,   -- workspace / RLS boundary (the enterprise)
    provider    NVARCHAR(10)     NOT NULL CHECK (provider IN ('Azure', 'Aws', 'Gcp')),
    name        NVARCHAR(256)    NOT NULL,
    external_id NVARCHAR(256)    NULL,        -- Azure tenant GUID / AWS Org ID / GCP Org ID
    status      NVARCHAR(20)     NOT NULL DEFAULT 'Active'
                  CHECK (status IN ('Active', 'Degraded', 'Disconnected')),
    created_at  DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    created_by  NVARCHAR(256)    NOT NULL DEFAULT 'system',
    INDEX IX_co_tenant (tenant_id, provider)
);
GO

-- Attach accounts to an org; keep tenant_id (workspace/RLS).
ALTER TABLE cloud_accounts ADD org_id UNIQUEIDENTIFIER NULL;
GO

-- Drop the FK that tied every cloud account to an Azure tenant row, so AWS/GCP
-- accounts no longer depend on onboarding an Azure tenant. (The constraint is
-- auto-named, so resolve it dynamically.)
DECLARE @fk NVARCHAR(256);
SELECT @fk = fk.name
FROM sys.foreign_keys fk
WHERE fk.parent_object_id = OBJECT_ID('dbo.cloud_accounts')
  AND fk.referenced_object_id = OBJECT_ID('dbo.tenants');
IF @fk IS NOT NULL
    EXEC('ALTER TABLE dbo.cloud_accounts DROP CONSTRAINT ' + @fk);
GO

-- Backfill: one org per (workspace, provider) that already has accounts,
-- then link those accounts to it.
INSERT INTO cloud_orgs (org_id, tenant_id, provider, name, created_by)
SELECT NEWID(), d.tenant_id, d.provider,
       CASE d.provider
         WHEN 'Aws' THEN 'AWS Organization'
         WHEN 'Gcp' THEN 'GCP Organization'
         ELSE 'Azure'
       END,
       'system'
FROM (SELECT DISTINCT tenant_id, provider FROM cloud_accounts WHERE org_id IS NULL) d;
GO

UPDATE ca
SET ca.org_id = co.org_id
FROM cloud_accounts ca
INNER JOIN cloud_orgs co
    ON co.tenant_id = ca.tenant_id AND co.provider = ca.provider
WHERE ca.org_id IS NULL;
GO

-- RLS: scope cloud_orgs to the workspace, same as every other tenant-scoped table.
ALTER SECURITY POLICY dbo.TenantSecurityPolicy
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.cloud_orgs,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.cloud_orgs;
GO
