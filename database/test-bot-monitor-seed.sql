-- Minimal BotMonitor rows for SQL integration tests (synthetic credentials only).
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

EXEC sp_set_session_context @key = N'bypass_rls', @value = 1;

DECLARE @TenantId UNIQUEIDENTIFIER = 'A1111111-1111-1111-1111-111111111111';
DECLARE @BotId UNIQUEIDENTIFIER = 'A2222222-2222-2222-2222-222222222222';
DECLARE @UserId UNIQUEIDENTIFIER = 'A1000000-0000-0000-0000-000000000001';

IF NOT EXISTS (SELECT 1 FROM organizations WHERE org_id = @TenantId)
    INSERT INTO organizations (org_id, name, environment, created_by)
    VALUES (@TenantId, N'BotMonitor SQL test org', N'Production', N'system');

IF NOT EXISTS (SELECT 1 FROM bot_monitor_tenants WHERE tenant_id = @TenantId)
    INSERT INTO bot_monitor_tenants (tenant_id, name) VALUES (@TenantId, N'BotMonitor SQL test org');

IF NOT EXISTS (SELECT 1 FROM bot_monitor_bots WHERE bot_id = @BotId)
    INSERT INTO bot_monitor_bots (bot_id, tenant_id, owner_user_id, display_name, platform, bot_type_key, token_hash, hmac_secret_enc, checkin_interval_minutes)
    VALUES (@BotId, @TenantId, @UserId, N'Grok integration bot', N'other', N'grokbot', REPLICATE('a', 64), N'enc', 60);

IF NOT EXISTS (SELECT 1 FROM bot_monitor_heartbeats WHERE bot_id = @BotId)
    INSERT INTO bot_monitor_heartbeats (bot_id, tenant_id, received_at, status, keep_check)
    VALUES (@BotId, @TenantId, SYSUTCDATETIME(), N'ok', N'pass');

IF NOT EXISTS (SELECT 1 FROM bot_monitor_bot_state WHERE bot_id = @BotId)
    INSERT INTO bot_monitor_bot_state (bot_id, tenant_id, last_seen_at, last_status)
    VALUES (@BotId, @TenantId, SYSUTCDATETIME(), N'ok');
GO
