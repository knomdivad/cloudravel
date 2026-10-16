-- Minimal BotMonitor schema for test-006-bot-monitor-org-fk.sh (CI only).

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF OBJECT_ID(N'dbo.organizations') IS NULL
BEGIN
    CREATE TABLE organizations (
        org_id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        name NVARCHAR(256) NOT NULL,
        environment NVARCHAR(20) NOT NULL DEFAULT N'Development',
        created_by NVARCHAR(128) NOT NULL DEFAULT N'system'
    );
END
GO

IF OBJECT_ID(N'dbo.bot_monitor_tenants') IS NULL
BEGIN
    CREATE TABLE bot_monitor_tenants (
        tenant_id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        name NVARCHAR(256) NOT NULL,
        created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END
GO

IF OBJECT_ID(N'dbo.bot_monitor_bots') IS NULL
BEGIN
    CREATE TABLE bot_monitor_bots (
        bot_id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        tenant_id UNIQUEIDENTIFIER NOT NULL REFERENCES bot_monitor_tenants(tenant_id),
        display_name NVARCHAR(256) NOT NULL,
        platform NVARCHAR(20) NOT NULL,
        token_hash NVARCHAR(128) NOT NULL,
        hmac_secret_enc NVARCHAR(512) NOT NULL,
        checkin_interval_minutes INT NOT NULL DEFAULT 60,
        created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        revoked_at DATETIME2 NULL,
        owner_user_id UNIQUEIDENTIFIER NULL
    );
END
GO

IF OBJECT_ID(N'dbo.bot_monitor_heartbeats') IS NULL
BEGIN
    CREATE TABLE bot_monitor_heartbeats (
        id BIGINT IDENTITY(1,1) PRIMARY KEY,
        bot_id UNIQUEIDENTIFIER NOT NULL REFERENCES bot_monitor_bots(bot_id),
        tenant_id UNIQUEIDENTIFIER NOT NULL,
        received_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        status NVARCHAR(20) NOT NULL,
        keep_check NVARCHAR(10) NOT NULL DEFAULT N'none'
    );
END
GO

IF OBJECT_ID(N'dbo.bot_monitor_bot_state') IS NULL
BEGIN
    CREATE TABLE bot_monitor_bot_state (
        bot_id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY REFERENCES bot_monitor_bots(bot_id),
        tenant_id UNIQUEIDENTIFIER NOT NULL,
        last_seen_at DATETIME2 NULL
    );
END
GO

IF OBJECT_ID(N'dbo.bot_monitor_nonces') IS NULL
BEGIN
    CREATE TABLE bot_monitor_nonces (
        bot_id UNIQUEIDENTIFIER NOT NULL,
        ts BIGINT NOT NULL,
        sig NVARCHAR(128) NOT NULL,
        created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        PRIMARY KEY (bot_id, ts, sig)
    );
END
GO

IF OBJECT_ID(N'dbo.bot_monitor_heartbeat_rate') IS NULL
BEGIN
    CREATE TABLE bot_monitor_heartbeat_rate (
        bot_id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        window_start DATETIME2 NOT NULL,
        request_count INT NOT NULL DEFAULT 0
    );
END
GO

IF OBJECT_ID(N'dbo.bot_monitor_magic_links') IS NULL
BEGIN
    CREATE TABLE bot_monitor_magic_links (
        link_id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        tenant_id UNIQUEIDENTIFIER NOT NULL REFERENCES bot_monitor_tenants(tenant_id),
        token_hash NVARCHAR(128) NOT NULL,
        expires_at DATETIME2 NOT NULL,
        redeemed_at DATETIME2 NULL,
        created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END
GO
