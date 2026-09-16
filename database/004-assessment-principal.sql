-- ============================================================================
-- 004-assessment-principal.sql — service-credential support for the
-- standalone assessment tool
--
-- The cloudravel-assessment repo reads CloudRavel through the app API using a
-- dedicated read-only service credential: a users row whose global_role is
-- 'service_principal' (AuthenticationProvider 'local', password held in the
-- operator's secrets store) granted ONLY 'read_only' role in user_tenant_access
-- for the tenants it may read. AssessmentPrincipalMiddleware enforces the
-- read-only/single-tenant/audit/rate-limit contract server-side.
--
-- Idempotent: safe to re-run against any volume state.
-- ============================================================================

-- Widen the global-role CHECK to admit the service-principal role.
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_users_global_role')
    ALTER TABLE dbo.users DROP CONSTRAINT CK_users_global_role;
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_users_global_role')
    ALTER TABLE dbo.users ADD CONSTRAINT CK_users_global_role
        CHECK (global_role IN ('system_admin', 'member', 'service_principal'));
GO
