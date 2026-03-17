-- ============================================================================
-- Migration 002: Fix RLS bypass_rls type mismatch
-- 
-- Problem: The security predicate compared bypass_rls (set as INT from C#)
-- against CAST(1 AS VARBINARY(1)). These are different sql_variant type
-- families, so the equality check always returned false, blocking all
-- admin writes.
--
-- Fix: Compare against INT instead of VARBINARY.
-- ============================================================================

-- Step 1: Drop the security policy (required before altering the function)
DROP SECURITY POLICY IF EXISTS dbo.TenantSecurityPolicy;
GO

-- Step 2: Drop and recreate the predicate function with correct type comparison
DROP FUNCTION IF EXISTS dbo.fn_tenant_security_predicate;
GO

CREATE FUNCTION dbo.fn_tenant_security_predicate(@tenant_id UNIQUEIDENTIFIER)
RETURNS TABLE
WITH SCHEMABINDING
AS
    RETURN SELECT 1 AS result
    WHERE @tenant_id = CAST(SESSION_CONTEXT(N'tenant_id') AS UNIQUEIDENTIFIER)
       OR CAST(SESSION_CONTEXT(N'bypass_rls') AS INT) = 1;
GO

-- Step 3: Recreate the security policy with all predicates
CREATE SECURITY POLICY dbo.TenantSecurityPolicy
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.inventory_snapshots,
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.inventory_resources,
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.resource_changes,
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.activity_log_events,
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.advisor_recommendations,
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.policy_compliance,
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.defender_findings,
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.ai_query_log,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.inventory_resources,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.resource_changes,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.activity_log_events,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.advisor_recommendations,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.policy_compliance,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.defender_findings,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.ai_query_log
    WITH (STATE = ON);
GO
