-- ============================================================================
-- Repair: ensure every Organization has a matching tenants workspace row
-- (user_tenant_access.tenant_id FK → tenants.tenant_id).
-- Also remaps legacy roles if 010-admin-rbac never finished.
-- ============================================================================

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

DECLARE @c NVARCHAR(256);

SELECT @c = cc.name FROM sys.check_constraints cc
WHERE cc.parent_object_id = OBJECT_ID('dbo.users') AND cc.definition LIKE '%global_role%';
IF @c IS NOT NULL EXEC('ALTER TABLE dbo.users DROP CONSTRAINT ' + @c);

UPDATE users SET global_role = 'system_admin' WHERE global_role = 'admin';
UPDATE users SET global_role = 'member' WHERE global_role IN ('operator', 'auditor');

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_users_global_role')
    ALTER TABLE users ADD CONSTRAINT CK_users_global_role CHECK (global_role IN ('system_admin', 'member'));

IF NOT EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_users_global_role')
    ALTER TABLE users ADD CONSTRAINT DF_users_global_role DEFAULT 'member' FOR global_role;

SET @c = NULL;
SELECT @c = cc.name FROM sys.check_constraints cc
WHERE cc.parent_object_id = OBJECT_ID('dbo.user_tenant_access') AND cc.definition LIKE '%role%';
IF @c IS NOT NULL EXEC('ALTER TABLE dbo.user_tenant_access DROP CONSTRAINT ' + @c);

UPDATE user_tenant_access SET role = 'org_admin'  WHERE role IN ('admin', 'customer_admin');
UPDATE user_tenant_access SET role = 'cloud_admin' WHERE role = 'operator';
UPDATE user_tenant_access SET role = 'read_only'  WHERE role IN ('auditor', 'customer_viewer');

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_uta_role')
    ALTER TABLE user_tenant_access
        ADD CONSTRAINT CK_uta_role CHECK (role IN ('org_admin', 'cloud_admin', 'read_only'));
GO

INSERT INTO tenants (
    tenant_id, display_name, azure_tenant_id, onboarding_method, status,
    snapshot_frequency_minutes, change_poll_frequency_minutes, created_by
)
SELECT
    o.org_id, o.name, '00000000-0000-0000-0000-000000000000', 'lighthouse', 'active',
    360, 15, 'repair-org-workspace-shell'
FROM organizations o
WHERE o.status = 'active'
  AND NOT EXISTS (SELECT 1 FROM tenants t WHERE t.tenant_id = o.org_id);
GO

-- Align blank emails / legacy admin username with email-as-login identity.
UPDATE users
SET email = LOWER(COALESCE(NULLIF(LTRIM(RTRIM(username)), ''), CAST(user_id AS NVARCHAR(36))) + '@local')
WHERE email IS NULL OR LTRIM(RTRIM(email)) = '';

UPDATE users SET email = LOWER(LTRIM(RTRIM(email)));

UPDATE users
SET username = 'admin@local', email = 'admin@local'
WHERE auth_provider = 'local' AND (username = 'admin' OR email IN ('admin', 'admin@local'));

UPDATE users SET username = email
WHERE auth_provider = 'local' AND (username IS NULL OR username <> email);
GO

INSERT INTO user_tenant_access (user_id, tenant_id, role, granted_by)
SELECT u.user_id, o.org_id, 'org_admin', u.user_id
FROM users u
CROSS JOIN organizations o
WHERE u.auth_provider = 'local' AND (u.username = 'admin@local' OR u.email = 'admin@local')
  AND o.status = 'active'
  AND NOT EXISTS (
      SELECT 1 FROM user_tenant_access uta
      WHERE uta.user_id = u.user_id AND uta.tenant_id = o.org_id
  );
GO

SELECT o.org_id, o.name AS org_name, t.tenant_id, t.azure_tenant_id
FROM organizations o
LEFT JOIN tenants t ON t.tenant_id = o.org_id
ORDER BY o.name;
GO
