-- ============================================================================
-- LOCAL DEVELOPMENT ONLY — BotMonitor demo rows for /ops/bots and /portal/bots.
-- Do not run against staging or production. Contains synthetic token hashes only.
--
-- Guard: abort unless the instance is explicitly Development.
-- ============================================================================

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF EXISTS (SELECT 1 FROM organizations WHERE environment = N'Production')
BEGIN
    RAISERROR(N'seed-bot-monitor-demo.sql is local-only and must not run when Production organizations exist.', 16, 1);
    RETURN;
END

EXEC sp_set_session_context @key = N'bypass_rls', @value = 1;

DECLARE @TenantId UNIQUEIDENTIFIER = 'B0000000-0000-0000-0000-000000000001';
DECLARE @BotA UNIQUEIDENTIFIER = 'B0000000-0000-0000-0000-000000000011';
DECLARE @BotB UNIQUEIDENTIFIER = 'B0000000-0000-0000-0000-000000000012';
DECLARE @Now DATETIME2 = SYSUTCDATETIME();
DECLARE @TokenHashA NVARCHAR(128) = LOWER(CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', 'demo-token-a-local-only'), 2));
DECLARE @TokenHashB NVARCHAR(128) = LOWER(CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', 'demo-token-b-local-only'), 2));
DECLARE @EncSecret NVARCHAR(512) = 'demo-not-for-production.enc';

IF EXISTS (SELECT 1 FROM bot_monitor_tenants WHERE tenant_id = @TenantId)
BEGIN
    PRINT 'BotMonitor demo tenant already present.';
    RETURN;
END

IF NOT EXISTS (SELECT 1 FROM organizations WHERE org_id = @TenantId)
    INSERT INTO organizations (org_id, name, environment, created_by)
    VALUES (@TenantId, N'cloudravel-internal', N'Development', N'system');

INSERT INTO bot_monitor_tenants (tenant_id, name) VALUES (@TenantId, N'cloudravel-internal');

DECLARE @AdminUserId UNIQUEIDENTIFIER = (SELECT TOP 1 user_id FROM users WHERE email = N'admin@local');
IF @AdminUserId IS NOT NULL AND NOT EXISTS (
    SELECT 1 FROM user_tenant_access WHERE user_id = @AdminUserId AND tenant_id = @TenantId)
    INSERT INTO user_tenant_access (user_id, tenant_id, role, granted_by)
    VALUES (@AdminUserId, @TenantId, N'org_admin', @AdminUserId);

INSERT INTO bot_monitor_bots
    (bot_id, tenant_id, display_name, platform, token_hash, hmac_secret_enc, checkin_interval_minutes)
VALUES
    (@BotA, @TenantId, N'Keep probe alpha', N'grok_bot', @TokenHashA, @EncSecret, 60),
    (@BotB, @TenantId, N'Mail collab beta', N'email', @TokenHashB, @EncSecret, 60);

INSERT INTO bot_monitor_heartbeats
    (bot_id, tenant_id, received_at, status, last_task, errors, keep_check, keep_check_detail)
VALUES
    (@BotA, @TenantId, DATEADD(MINUTE, -5, @Now), N'ok', N'Weekly Keep checks', NULL, N'pass', N'All probes succeeded'),
    (@BotB, @TenantId, DATEADD(HOUR, -3, @Now), N'error', N'Inbox polling', N'["timeout"]', N'warn', N'Retry scheduled');

INSERT INTO bot_monitor_bot_state (bot_id, tenant_id, last_seen_at, last_status, last_error, last_error_at)
VALUES
    (@BotA, @TenantId, DATEADD(MINUTE, -5, @Now), N'ok', NULL, NULL),
    (@BotB, @TenantId, DATEADD(HOUR, -3, @Now), N'error', N'timeout', DATEADD(HOUR, -3, @Now));

PRINT 'BotMonitor demo seed complete (local development only).';
