-- ============================================================================
-- Migration 009: Azure as a peer cloud_orgs connection (multiple Azure tenants
-- per Organization)
--
-- Until now, an Organization could hold only ONE Azure tenant, because Azure
-- credentials/onboarding lived on the `tenants` table, whose primary key
-- (tenant_id) IS the workspace/RLS boundary — one row per workspace, full stop.
--
-- This migration makes Azure a peer of AWS/GCP in cloud_orgs, the same way AWS
-- Organizations and GCP Organizations already are: N Azure tenant CONNECTIONS
-- (cloud_orgs rows, provider='Azure') per workspace, each with its own
-- onboarding method, credentials, and subscription scope (all vs specific).
--
-- The legacy `tenants` row is NOT removed — it still carries workspace-level
-- policy (AutoRemediationMode, AiOpsMonitoringEnabled, snapshot/change-poll
-- cadence) that the scheduler timers key off, and it remains the credential
-- source for change-polling / Advisor-Policy-Defender sync / Azure remediation
-- execution (which still operate against a workspace's PRIMARY Azure
-- connection only — a follow-up would extend those the same way inventory
-- collection is extended here). Inventory collection now loops every Azure
-- cloud_orgs connection for the workspace instead of the tenants row alone.
--
-- Run after 001–008. Sets bypass_rls so the backfill can read/write the
-- RLS-protected cloud_orgs table.
-- ============================================================================

EXEC sp_set_session_context @key = N'bypass_rls', @value = 1;
GO

-- Azure-only connection fields, nullable/no-op for Aws/Gcp rows.
ALTER TABLE cloud_orgs ADD
    onboarding_method       NVARCHAR(20)  NULL
        CHECK (onboarding_method IN ('lighthouse', 'app_registration')),
    credential_secret_name  NVARCHAR(256) NULL,
    lighthouse_delegation_id NVARCHAR(256) NULL,
    subscription_scope     NVARCHAR(10)  NOT NULL
        CONSTRAINT DF_co_subscription_scope DEFAULT 'all'
        CHECK (subscription_scope IN ('all', 'specific'));
GO

-- Pinned subscriptions for an Azure connection whose subscription_scope='specific'.
-- (Only meaningful for provider='Azure' cloud_orgs rows; AWS/GCP use cloud_accounts
-- instead, since their members need independent credentials/health, unlike an Azure
-- subscription, which is just a scope filter under its connection's one credential.)
CREATE TABLE azure_org_subscriptions (
    id                  BIGINT IDENTITY(1,1) PRIMARY KEY,
    org_id              UNIQUEIDENTIFIER NOT NULL REFERENCES cloud_orgs(org_id),
    tenant_id           UNIQUEIDENTIFIER NOT NULL,   -- workspace / RLS boundary
    subscription_id     NVARCHAR(36)     NOT NULL,
    subscription_name   NVARCHAR(256)    NOT NULL,
    created_at          DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    UNIQUE (org_id, subscription_id),
    INDEX IX_aos_org (org_id)
);
GO

ALTER SECURITY POLICY dbo.TenantSecurityPolicy
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.azure_org_subscriptions,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.azure_org_subscriptions;
GO

-- ============================================================================
-- Backfill: mirror every existing tenants row (the workspace's sole, legacy
-- Azure connection) into a peer cloud_orgs row, so the new collector loop
-- picks up existing deployments with zero data loss and zero re-onboarding.
-- ============================================================================

DECLARE @AzureOrgMap TABLE (tenant_id UNIQUEIDENTIFIER, org_id UNIQUEIDENTIFIER);

INSERT INTO cloud_orgs (org_id, tenant_id, provider, name, external_id, status,
                         onboarding_method, credential_secret_name, lighthouse_delegation_id,
                         subscription_scope, created_by)
OUTPUT inserted.tenant_id, inserted.org_id INTO @AzureOrgMap (tenant_id, org_id)
SELECT
    NEWID(),
    t.tenant_id,
    'Azure',
    t.display_name,
    t.azure_tenant_id,
    CASE t.status WHEN 'active' THEN 'Active' WHEN 'degraded' THEN 'Degraded' ELSE 'Disconnected' END,
    t.onboarding_method,
    t.secret_name,
    t.lighthouse_delegation_id,
    CASE WHEN EXISTS (SELECT 1 FROM tenant_subscriptions ts WHERE ts.tenant_id = t.tenant_id)
         THEN 'specific' ELSE 'all' END,
    'system-migration'
FROM tenants t
WHERE NOT EXISTS (
    SELECT 1 FROM cloud_orgs co
    WHERE co.tenant_id = t.tenant_id AND co.provider = 'Azure' AND co.external_id = t.azure_tenant_id
);

-- No GO above: @AzureOrgMap must stay in scope for this second insert.
INSERT INTO azure_org_subscriptions (org_id, tenant_id, subscription_id, subscription_name)
SELECT m.org_id, ts.tenant_id, ts.subscription_id, ts.subscription_name
FROM tenant_subscriptions ts
INNER JOIN @AzureOrgMap m ON m.tenant_id = ts.tenant_id;
GO
