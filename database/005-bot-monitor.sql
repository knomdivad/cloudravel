-- ============================================================================
-- BotMonitor module: bot registry, signed heartbeats, operator and portal views
-- ============================================================================

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

CREATE TABLE bot_monitor_tenants (
    tenant_id   UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    name        NVARCHAR(256)    NOT NULL,
    created_at  DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

CREATE TABLE bot_monitor_bots (
    bot_id                      UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    tenant_id                   UNIQUEIDENTIFIER NOT NULL REFERENCES bot_monitor_tenants(tenant_id),
    display_name                NVARCHAR(256)    NOT NULL,
    platform                    NVARCHAR(20)     NOT NULL
        CONSTRAINT CK_bot_monitor_bots_platform CHECK (platform IN ('grok_bot', 'email', 'other')),
    token_hash                  NVARCHAR(128)    NOT NULL,
    hmac_secret_enc             NVARCHAR(512)    NOT NULL,
    checkin_interval_minutes    INT              NOT NULL DEFAULT 60,
    created_at                  DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    revoked_at                  DATETIME2        NULL,
    INDEX IX_bmb_tenant (tenant_id),
    INDEX IX_bmb_token_hash (token_hash)
);
GO

CREATE TABLE bot_monitor_heartbeats (
    id                  BIGINT IDENTITY(1,1) PRIMARY KEY,
    bot_id              UNIQUEIDENTIFIER NOT NULL REFERENCES bot_monitor_bots(bot_id),
    tenant_id           UNIQUEIDENTIFIER NOT NULL,
    received_at         DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    status              NVARCHAR(20)     NOT NULL
        CONSTRAINT CK_bot_monitor_hb_status CHECK (status IN ('ok', 'busy', 'error', 'offline')),
    last_task           NVARCHAR(500)    NULL,
    errors              NVARCHAR(MAX)    NULL,
    keep_check          NVARCHAR(10)     NOT NULL DEFAULT 'none'
        CONSTRAINT CK_bot_monitor_hb_keep CHECK (keep_check IN ('pass', 'warn', 'fail', 'none')),
    keep_check_detail   NVARCHAR(1000)   NULL,
    INDEX IX_bmh_bot_received (bot_id, received_at DESC),
    INDEX IX_bmh_tenant_received (tenant_id, received_at DESC)
);
GO

CREATE TABLE bot_monitor_bot_state (
    bot_id          UNIQUEIDENTIFIER NOT NULL PRIMARY KEY REFERENCES bot_monitor_bots(bot_id),
    tenant_id       UNIQUEIDENTIFIER NOT NULL,
    last_seen_at    DATETIME2        NULL,
    last_status     NVARCHAR(20)     NULL,
    last_error      NVARCHAR(500)    NULL,
    last_error_at   DATETIME2        NULL
);
GO

CREATE TABLE bot_monitor_nonces (
    bot_id      UNIQUEIDENTIFIER NOT NULL,
    ts          BIGINT           NOT NULL,
    sig         NVARCHAR(128)    NOT NULL,
    created_at  DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    PRIMARY KEY (bot_id, ts, sig)
);
GO

CREATE INDEX IX_bmn_created ON bot_monitor_nonces(created_at);
GO

CREATE TABLE bot_monitor_heartbeat_rate (
    bot_id          UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    window_start    DATETIME2        NOT NULL,
    request_count   INT              NOT NULL DEFAULT 0
);
GO

-- Optional portal magic-link stub (feature-flagged at the app layer)
CREATE TABLE bot_monitor_magic_links (
    link_id         UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    tenant_id       UNIQUEIDENTIFIER NOT NULL REFERENCES bot_monitor_tenants(tenant_id),
    token_hash      NVARCHAR(128)    NOT NULL,
    expires_at      DATETIME2        NOT NULL,
    redeemed_at     DATETIME2        NULL,
    created_at      DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

ALTER SECURITY POLICY dbo.TenantSecurityPolicy
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.bot_monitor_bots,
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.bot_monitor_heartbeats,
    ADD FILTER PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.bot_monitor_bot_state,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.bot_monitor_bots,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.bot_monitor_heartbeats,
    ADD BLOCK PREDICATE dbo.fn_tenant_security_predicate(tenant_id) ON dbo.bot_monitor_bot_state;
GO
