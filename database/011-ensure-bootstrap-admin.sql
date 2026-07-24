-- ============================================================================
-- Migration 011: Ensure a bootstrap system_admin always exists
--
-- Safety net against an unreachable admin UI: if every user somehow ends up
-- without the system_admin role (a botched manual edit, a role remap gone
-- wrong, etc.), the /admin and /organization pages become permanently
-- unreachable through the UI itself, since granting system_admin currently
-- requires already being a system_admin.
--
-- Idempotent and non-destructive: only acts when NO system_admin exists at
-- all. Prefers the seeded local 'admin' user (see 004-local-auth.sql);
-- falls back to the oldest local user otherwise. Never touches role
-- assignments while at least one system_admin is present.
--
-- Run after 001-010.
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM users WHERE global_role = 'system_admin')
BEGIN
    UPDATE TOP (1) users SET global_role = 'system_admin'
    WHERE user_id = (
        SELECT TOP (1) user_id FROM users
        WHERE auth_provider = 'local'
        ORDER BY CASE WHEN username = 'admin' THEN 0 ELSE 1 END, created_at
    );
END
GO
