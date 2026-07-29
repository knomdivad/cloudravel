-- ============================================================================
-- Migration 012: RLS hardening
--
-- - Add BLOCK predicate on inventory_snapshots (FILTER already present)
-- - Apply FILTER + BLOCK on audit_events for non-null tenant_id rows
-- - job_queue remains admin/worker-only (no tenant_id column) — not RLS-scoped
--
-- Run after 001-011.
-- ============================================================================

-- inventory_snapshots: prevent cross-tenant writes without matching session context
ALTER SECURITY POLICY dbo.TenantSecurityPolicy
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.inventory_snapshots;
GO

-- audit_events: tenant-scoped rows are isolated; global (NULL tenant_id) rows
-- require bypass_rls (admin connection), which AuditRepository already uses.
ALTER SECURITY POLICY dbo.TenantSecurityPolicy
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.audit_events,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.audit_events;
GO
