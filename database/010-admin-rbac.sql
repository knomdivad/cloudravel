-- ============================================================================
-- Migration 010: RBAC roles + system settings + per-organization SSO settings
--
-- Introduces a two-tier role model and the tables backing the admin interfaces:
--   * users.global_role            -> system_admin | member   (the SYSTEM tier)
--   * user_tenant_access.role      -> org_admin | cloud_admin | read_only (ORG tier)
--   * system_settings              -> global key/value config (e.g. OpenAI settings)
--   * org_sso_settings             -> per-org SSO configuration (stored; enforcement
--                                     is a documented follow-up)
--
-- Only the seeded 'admin' user and zero user_tenant_access rows exist at write
-- time, so the remap is near-zero-risk; the remaps are still written defensively
-- for any real deployment that already has data.
--
-- Order matters: drop the old CHECK constraints BEFORE remapping role values.
-- The legacy CHECK only allows admin/operator/auditor — writing system_admin
-- while it is still in place fails with Msg 547.
--
-- Run after 001-009.
-- ============================================================================

-- Filtered index on users(username) (004-local-auth) requires QUOTED_IDENTIFIER ON.
-- Classic sqlcmd defaults it OFF unless -I is passed; set explicitly so this
-- migration is safe under either invocation.
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

-- ----------------------------------------------------------------------------
-- 1. users.global_role : admin/operator/auditor  ->  system_admin/member
--    Drop old CHECK/DEFAULT first, then remap, then install the new constraints.
-- ----------------------------------------------------------------------------
DECLARE @c NVARCHAR(256);

-- Drop the auto-named CHECK constraint on global_role (or a prior run's named one).
SELECT @c = cc.name
FROM sys.check_constraints cc
WHERE cc.parent_object_id = OBJECT_ID('dbo.users')
  AND cc.definition LIKE '%global_role%';
IF @c IS NOT NULL EXEC('ALTER TABLE dbo.users DROP CONSTRAINT ' + @c);

-- Drop the auto-named DEFAULT constraint on global_role.
SET @c = NULL;
SELECT @c = dc.name
FROM sys.default_constraints dc
INNER JOIN sys.columns col
    ON col.object_id = dc.parent_object_id AND col.column_id = dc.parent_column_id
WHERE dc.parent_object_id = OBJECT_ID('dbo.users') AND col.name = 'global_role';
IF @c IS NOT NULL EXEC('ALTER TABLE dbo.users DROP CONSTRAINT ' + @c);

-- Remap only while unconstrained (idempotent for already-remapped rows).
UPDATE users SET global_role = 'system_admin' WHERE global_role = 'admin';
UPDATE users SET global_role = 'member'       WHERE global_role IN ('operator', 'auditor');

-- Install new DEFAULT + CHECK if missing (safe re-run).
IF NOT EXISTS (SELECT 1 FROM sys.default_constraints
               WHERE parent_object_id = OBJECT_ID('dbo.users')
                 AND name = 'DF_users_global_role')
    ALTER TABLE users ADD CONSTRAINT DF_users_global_role DEFAULT 'member' FOR global_role;

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
               WHERE parent_object_id = OBJECT_ID('dbo.users')
                 AND name = 'CK_users_global_role')
    ALTER TABLE users ADD CONSTRAINT CK_users_global_role CHECK (global_role IN ('system_admin', 'member'));
GO

-- ----------------------------------------------------------------------------
-- 2. user_tenant_access.role : (admin|operator|auditor|customer_admin|customer_viewer)
--    -> org_admin | cloud_admin | read_only
-- ----------------------------------------------------------------------------
DECLARE @c2 NVARCHAR(256);
SELECT @c2 = cc.name
FROM sys.check_constraints cc
WHERE cc.parent_object_id = OBJECT_ID('dbo.user_tenant_access')
  AND cc.definition LIKE '%role%';
IF @c2 IS NOT NULL EXEC('ALTER TABLE dbo.user_tenant_access DROP CONSTRAINT ' + @c2);

UPDATE user_tenant_access SET role = 'org_admin'  WHERE role IN ('admin', 'customer_admin');
UPDATE user_tenant_access SET role = 'cloud_admin' WHERE role = 'operator';
UPDATE user_tenant_access SET role = 'read_only'  WHERE role IN ('auditor', 'customer_viewer');

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
               WHERE parent_object_id = OBJECT_ID('dbo.user_tenant_access')
                 AND name = 'CK_uta_role')
    ALTER TABLE user_tenant_access
        ADD CONSTRAINT CK_uta_role CHECK (role IN ('org_admin', 'cloud_admin', 'read_only'));
GO

-- ----------------------------------------------------------------------------
-- 3. system_settings — global key/value config (NOT RLS-scoped; system-admin only
--    at the app layer). Secret values (e.g. the OpenAI API key) are NOT stored
--    here; only a secret NAME pointing at the secret store, mirroring cloud creds.
-- ----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.system_settings', N'U') IS NULL
BEGIN
    CREATE TABLE system_settings (
        setting_key   NVARCHAR(128) NOT NULL PRIMARY KEY,
        setting_value NVARCHAR(MAX) NULL,
        updated_at    DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
        updated_by    NVARCHAR(256) NOT NULL DEFAULT 'system'
    );
END
GO

-- ----------------------------------------------------------------------------
-- 4. org_sso_settings — per-organization SSO config. Stored now; per-org token
--    federation (multi-issuer validation, user->org mapping) is a follow-up.
--    Any client secret goes to the secret store; only its name is kept here.
-- ----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.org_sso_settings', N'U') IS NULL
BEGIN
    CREATE TABLE org_sso_settings (
        org_id             UNIQUEIDENTIFIER NOT NULL PRIMARY KEY REFERENCES organizations(org_id),
        provider           NVARCHAR(20)     NOT NULL DEFAULT 'none'
                             CHECK (provider IN ('none', 'entra', 'oidc')),
        idp_tenant_id      NVARCHAR(256)    NULL,
        idp_client_id      NVARCHAR(256)    NULL,
        domain             NVARCHAR(256)    NULL,
        client_secret_name NVARCHAR(256)    NULL,
        enabled            BIT              NOT NULL DEFAULT 0,
        updated_at         DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        updated_by         NVARCHAR(256)    NOT NULL DEFAULT 'system'
    );
END
GO
